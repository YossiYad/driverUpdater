using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using DriverUpdater.Services.Sources;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriverUpdater.Services.Tests.Sources;

public class WingetVendorToolSourceTests
{
    [Fact]
    public void TryParsePackageRow_reads_installed_and_available_versions()
    {
        const string output = """
            Name           Id            Version       Available     Source
            ---------------------------------------------------------------
            Logitech G HUB Logitech.GHUB 2026.3.880543 2026.4.919028 winget
            """;

        var found = WingetVendorToolSource.TryParsePackageRow(
            output,
            "Logitech.GHUB",
            out var installed,
            out var available);

        found.Should().BeTrue();
        installed.Should().Be("2026.3.880543");
        available.Should().Be("2026.4.919028");
    }

    [Fact]
    public void TryParsePackageRow_reads_current_package_without_an_available_column()
    {
        const string output = """
            Name             Id             Version Source
            -----------------------------------------------
            ViGEm Bus Driver ViGEm.ViGEmBus 1.22.0  winget
            """;

        var found = WingetVendorToolSource.TryParsePackageRow(
            output,
            "ViGEm.ViGEmBus",
            out var installed,
            out var available);

        found.Should().BeTrue();
        installed.Should().Be("1.22.0");
        available.Should().BeNull();
    }

    [Fact]
    public async Task SearchAsync_returns_one_shared_upgrade_for_each_matching_logitech_device()
    {
        using var tool = new TemporaryToolFile();
        var runner = new ScriptedRunner(arguments => arguments.StartsWith("list ", StringComparison.Ordinal)
            ? new ProcessResult(0, "Logitech G HUB Logitech.GHUB 2026.3.880543 2026.4.919028 winget", string.Empty)
            : new ProcessResult(1, string.Empty, "unexpected"));
        var source = NewSource(tool.Path, runner);
        var drivers = new[]
        {
            NewDriver("LIGHTSPEED Receiver", "USB\\VID_046D&PID_C547", "Microsoft"),
            NewDriver("Logitech G HUB Virtual Bus Enumerator", "ROOT\\LGHUB\\0000", "Logitech")
        };

        var results = await source.SearchAsync(drivers).ToListAsync();

        results.Should().HaveCount(2);
        results.Select(candidate => candidate.SourceUpdateId).Should().OnlyContain(id =>
            id == "vendor-installer:winget:upgrade:Logitech.GHUB:2026.4.919028");
        results.Select(candidate => candidate.DownloadUrl.LocalPath).Should().OnlyContain(path => path == tool.Path);
        results.Select(candidate => candidate.NewVersion).Should().OnlyContain(version =>
            version == new Version(2026, 4, 919028));
        runner.Arguments.Should().ContainSingle(argument => argument.Contains("list --id \"Logitech.GHUB\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SearchAsync_offers_install_when_matching_vendor_tool_is_missing()
    {
        using var tool = new TemporaryToolFile();
        var runner = new ScriptedRunner(arguments => arguments.StartsWith("search ", StringComparison.Ordinal)
            ? new ProcessResult(0, "SteelSeries GG SteelSeries.GG 116.0.0 winget", string.Empty)
            : new ProcessResult(1, "No installed package found matching input criteria.", string.Empty));
        var source = NewSource(tool.Path, runner);

        var results = await source.SearchAsync(new[]
        {
            NewDriver("HID-compliant consumer control device", "HID\\VID_1038&PID_2232", "Microsoft")
        }).ToListAsync();

        results.Should().ContainSingle();
        results[0].SourceUpdateId.Should().Be("vendor-installer:winget:install:SteelSeries.GG:116.0.0");
        results[0].VersionLabel.Should().Be("SteelSeries GG 116.0.0");
        runner.Arguments.Should().Contain(argument => argument.Contains("search --id \"SteelSeries.GG\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SearchAsync_returns_nothing_when_package_is_current()
    {
        using var tool = new TemporaryToolFile();
        var runner = new ScriptedRunner(_ =>
            new ProcessResult(0, "ViGEm Bus Driver ViGEm.ViGEmBus 1.22.0 winget", string.Empty));
        var source = NewSource(tool.Path, runner);

        var results = await source.SearchAsync(new[]
        {
            NewDriver("Nefarius Virtual Gamepad Emulation Bus", "ROOT\\VIGEMBUS\\0000", "Nefarius Software Solutions")
        }).ToListAsync();

        results.Should().BeEmpty();
        runner.Arguments.Should().ContainSingle();
    }

    [Fact]
    public async Task SearchAsync_rejects_a_non_microsoft_winget_binary()
    {
        using var tool = new TemporaryToolFile();
        var runner = new ScriptedRunner(_ => new ProcessResult(0, string.Empty, string.Empty));
        var source = new WingetVendorToolSource(
            runner,
            new TrustedSignatureVerifier("CN=Unexpected Publisher"),
            NullLogger<WingetVendorToolSource>.Instance,
            wingetLocator: () => tool.Path);

        var results = await source.SearchAsync(new[]
        {
            NewDriver("Tailscale Tunnel", "ROOT\\NET\\0001", "Tailscale")
        }).ToListAsync();

        results.Should().BeEmpty();
        runner.Arguments.Should().BeEmpty();
    }

    private static WingetVendorToolSource NewSource(string toolPath, IVendorInstallerRunner runner) =>
        new(
            runner,
            new TrustedSignatureVerifier("CN=Microsoft Corporation"),
            NullLogger<WingetVendorToolSource>.Instance,
            wingetLocator: () => toolPath);

    private static DriverInfo NewDriver(string name, string hardwareId, string provider) => new(
        DeviceId: hardwareId + "\\INSTANCE",
        HardwareId: hardwareId,
        DeviceName: name,
        Category: DriverCategory.System,
        Provider: provider,
        Manufacturer: provider,
        CurrentVersion: new Version(1, 0),
        CurrentDate: new DateOnly(2024, 1, 1),
        InfName: "oem1.inf",
        InfPath: null,
        IsSigned: true,
        DeviceClass: "SYSTEM");

    private sealed class ScriptedRunner(Func<string, ProcessResult> resultFactory) : IVendorInstallerRunner
    {
        public List<string> Arguments { get; } = [];

        public Task<ProcessResult> RunAsync(string fileName, string arguments, CancellationToken cancellationToken = default)
        {
            Arguments.Add(arguments);
            return Task.FromResult(resultFactory(arguments));
        }
    }

    private sealed class TrustedSignatureVerifier(string publisher) : IFileSignatureVerifier
    {
        public FileSignatureVerification Verify(string filePath) =>
            new(true, publisher, "ABC123", null);
    }

    private sealed class TemporaryToolFile : IDisposable
    {
        public TemporaryToolFile()
        {
            var directory = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "DriverUpdaterTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            Path = System.IO.Path.Combine(directory, "winget.exe");
            File.WriteAllText(Path, string.Empty);
        }

        public string Path { get; }

        public void Dispose()
        {
            var directory = System.IO.Path.GetDirectoryName(Path)!;
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
