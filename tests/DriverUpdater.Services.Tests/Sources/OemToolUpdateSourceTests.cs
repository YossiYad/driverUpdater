using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using DriverUpdater.Services.Sources;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriverUpdater.Services.Tests.Sources;

public class OemToolUpdateSourceTests
{
    [Fact]
    public async Task SearchAsync_returns_confirmed_vendor_installer_for_known_oem_cli()
    {
        var toolPath = Path.Combine(Path.GetTempPath(), "dcu-cli.exe");
        await File.WriteAllTextAsync(toolPath, string.Empty);
        try
        {
            var source = new OemToolUpdateSource(
                new FakeOemDetectionService(new OemInfo(
                    OemVendor.Dell,
                    "Dell",
                    "XPS",
                    "Dell Command Update",
                    toolPath,
                    new Uri("https://www.dell.com/support"))),
                NullLogger<OemToolUpdateSource>.Instance,
                new DellReportingRunner(updateCount: 2),
                new TrustedSignatureVerifier("CN=Dell Inc."));

            var results = await CollectAsync(source.SearchAsync(new[] { NewDriver() }));

            results.Should().ContainSingle();
            results[0].InstallKind.Should().Be(UpdateInstallKind.VendorInstaller);
            results[0].Confidence.Should().Be(UpdateConfidence.Confirmed);
            results[0].DownloadUrl.IsFile.Should().BeTrue();
            results[0].SourceUpdateId.Should().StartWith("vendor-installer:oem-tool:dell-command-update:");
            results[0].SourceUpdateId.Should().EndWith(":2");
        }
        finally
        {
            File.Delete(toolPath);
        }
    }

    [Fact]
    public async Task SearchAsync_returns_nothing_when_verified_oem_scan_has_no_updates()
    {
        var toolDirectory = Path.Combine(Path.GetTempPath(), "DriverUpdaterTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(toolDirectory);
        var toolPath = Path.Combine(toolDirectory, "dcu-cli.exe");
        await File.WriteAllTextAsync(toolPath, string.Empty);
        try
        {
            var source = new OemToolUpdateSource(
                new FakeOemDetectionService(new OemInfo(
                    OemVendor.Dell,
                    "Dell",
                    "Latitude",
                    "Dell Command Update",
                    toolPath,
                    new Uri("https://www.dell.com/support"))),
                NullLogger<OemToolUpdateSource>.Instance,
                new DellReportingRunner(updateCount: 0),
                new TrustedSignatureVerifier("CN=Dell Inc."));

            var results = await CollectAsync(source.SearchAsync(new[] { NewDriver() }));

            results.Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(toolDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task SearchAsync_rejects_oem_tool_signed_by_wrong_publisher()
    {
        var toolDirectory = Path.Combine(Path.GetTempPath(), "DriverUpdaterTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(toolDirectory);
        var toolPath = Path.Combine(toolDirectory, "dcu-cli.exe");
        await File.WriteAllTextAsync(toolPath, string.Empty);
        try
        {
            var runner = new DellReportingRunner(updateCount: 1);
            var source = new OemToolUpdateSource(
                new FakeOemDetectionService(new OemInfo(
                    OemVendor.Dell,
                    "Dell",
                    "Latitude",
                    "Dell Command Update",
                    toolPath,
                    new Uri("https://www.dell.com/support"))),
                NullLogger<OemToolUpdateSource>.Instance,
                runner,
                new TrustedSignatureVerifier("CN=Unknown Publisher"));

            var results = await CollectAsync(source.SearchAsync(new[] { NewDriver() }));

            results.Should().BeEmpty();
            runner.Invocations.Should().Be(0);
        }
        finally
        {
            Directory.Delete(toolDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task SearchAsync_ignores_installed_oem_tool_without_known_silent_cli()
    {
        var toolPath = Path.Combine(Path.GetTempPath(), "GCC.exe");
        await File.WriteAllTextAsync(toolPath, string.Empty);
        try
        {
            var source = new OemToolUpdateSource(
                new FakeOemDetectionService(new OemInfo(
                    OemVendor.Gigabyte,
                    "Gigabyte",
                    "B850M",
                    "GIGABYTE Control Center",
                    toolPath,
                    new Uri("https://www.gigabyte.com/support"))),
                NullLogger<OemToolUpdateSource>.Instance);

            var results = await CollectAsync(source.SearchAsync(new[] { NewDriver() }));

            results.Should().BeEmpty();
        }
        finally
        {
            File.Delete(toolPath);
        }
    }

    private static DriverInfo NewDriver() => new(
        DeviceId: "PCI\\VEN_1234",
        HardwareId: "PCI\\VEN_1234",
        DeviceName: "Test Chipset",
        Category: DriverCategory.Chipset,
        Provider: "Microsoft",
        Manufacturer: "Microsoft",
        CurrentVersion: new Version(1, 0),
        CurrentDate: new DateOnly(2024, 1, 1),
        InfName: "oem1.inf",
        InfPath: null,
        IsSigned: true,
        DeviceClass: "System");

    private static async Task<List<UpdateCandidate>> CollectAsync(IAsyncEnumerable<UpdateCandidate> candidates)
    {
        var results = new List<UpdateCandidate>();
        await foreach (var candidate in candidates)
        {
            results.Add(candidate);
        }
        return results;
    }

    private sealed class FakeOemDetectionService : IOemDetectionService
    {
        private readonly OemInfo? _oem;

        public FakeOemDetectionService(OemInfo? oem)
        {
            _oem = oem;
        }

        public Task<OemInfo?> DetectAsync(CancellationToken cancellationToken = default) => Task.FromResult(_oem);
    }

    private sealed class DellReportingRunner(int updateCount) : IVendorInstallerRunner
    {
        public int Invocations { get; private set; }

        public Task<ProcessResult> RunAsync(string fileName, string arguments, CancellationToken cancellationToken = default)
        {
            Invocations++;
            const string marker = "-report=\"";
            var start = arguments.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (start >= 0)
            {
                start += marker.Length;
                var end = arguments.IndexOf('"', start);
                var reportPath = arguments[start..end];
                Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
                var updates = string.Concat(Enumerable.Range(0, updateCount).Select(index => $"<update id=\"{index}\" />"));
                File.WriteAllText(reportPath, $"<updates>{updates}</updates>");
            }
            return Task.FromResult(new ProcessResult(0, "scan complete", string.Empty));
        }
    }

    private sealed class TrustedSignatureVerifier(string publisher) : IFileSignatureVerifier
    {
        public FileSignatureVerification Verify(string filePath) =>
            new(true, publisher, "ABC123", null);
    }
}
