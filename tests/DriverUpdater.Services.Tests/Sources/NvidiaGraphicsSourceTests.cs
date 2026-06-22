using System.Net;
using System.Runtime.InteropServices;
using DriverUpdater.Core.Models;
using DriverUpdater.Services.Sources;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriverUpdater.Services.Tests.Sources;

public class NvidiaGraphicsSourceTests
{
    private const string SampleJson = """
        {
          "Success": "1",
          "IDS": [{
            "downloadInfo": {
              "Success": "1",
              "Name": "GeForce%20Game%20Ready%20Driver",
              "Version": "610.47",
              "ReleaseDateTime": "Tue May 26, 2026",
              "DownloadURL": "https://us.download.nvidia.com/Windows/610.47/610.47-desktop-win10-win11-64bit-international-dch-whql.exe",
              "DownloadURLFileSize": "978.47 MB",
              "IsWHQL": "1"
            }
          }]
        }
        """;

    [Fact]
    public async Task SearchAsync_yields_vendor_installer_for_geforce_when_driver_is_older()
    {
        var source = NewSource(SampleJson);
        var driver = NewNvidiaDriver("NVIDIA GeForce RTX 4080", new DateOnly(2024, 10, 1));

        var results = await source.SearchAsync(new[] { driver }).ToListAsync();

        results.Should().ContainSingle();
        results[0].InstallKind.Should().Be(UpdateInstallKind.VendorInstaller);
        results[0].SourceUpdateId.Should().Be("vendor-installer:nvidia:610.47");
        results[0].DownloadUrl.AbsoluteUri.Should().Be("https://us.download.nvidia.com/Windows/610.47/610.47-desktop-win10-win11-64bit-international-dch-whql.exe");
        results[0].NewDate.Should().Be(new DateOnly(2026, 5, 26));
        results[0].SizeBytes.Should().BeGreaterThan(900_000_000); // ~978 MB
    }

    [Fact]
    public async Task SearchAsync_skips_when_local_driver_already_newer_or_equal()
    {
        var source = NewSource(SampleJson);
        var driver = NewNvidiaDriver("NVIDIA GeForce RTX 4080", new DateOnly(2026, 5, 26));

        var results = await source.SearchAsync(new[] { driver }).ToListAsync();

        results.Should().BeEmpty();
    }

    [Theory]
    [InlineData("AMD Radeon RX 7700 XT", "Advanced Micro Devices, Inc.")]
    [InlineData("Intel Iris Xe Graphics", "Intel Corporation")]
    [InlineData("Microsoft Basic Display Driver", "Microsoft")]
    public async Task SearchAsync_skips_non_nvidia_displays(string deviceName, string provider)
    {
        var source = NewSource(SampleJson);
        var driver = new DriverInfo(
            DeviceId: $"PCI\\{deviceName}",
            HardwareId: $"PCI\\{deviceName}",
            DeviceName: deviceName,
            Category: DriverCategory.Display,
            Provider: provider,
            Manufacturer: provider,
            CurrentVersion: new Version(1, 0, 0, 0),
            CurrentDate: new DateOnly(2024, 1, 1),
            InfName: "oem.inf",
            InfPath: null,
            IsSigned: true,
            DeviceClass: "DISPLAY");

        var results = await source.SearchAsync(new[] { driver }).ToListAsync();

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchAsync_skips_quadro_workstation_cards()
    {
        var source = NewSource(SampleJson);
        var driver = NewNvidiaDriver("NVIDIA Quadro RTX A6000", new DateOnly(2024, 1, 1));

        var results = await source.SearchAsync(new[] { driver }).ToListAsync();

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchAsync_logs_warning_and_yields_nothing_when_network_fails()
    {
        var source = new NvidiaGraphicsSource(
            new HttpClient(new ThrowingHandler()) { BaseAddress = new Uri("https://gfwsl.geforce.com/") },
            NullLogger<NvidiaGraphicsSource>.Instance);
        var driver = NewNvidiaDriver("NVIDIA GeForce RTX 4080", new DateOnly(2024, 10, 1));

        var results = await source.SearchAsync(new[] { driver }).ToListAsync();

        results.Should().BeEmpty();
    }

    [Fact]
    public void TryParseLatestRelease_reads_version_date_url_and_size()
    {
        var ok = NvidiaGraphicsSource.TryParseLatestRelease(SampleJson, out var release);

        ok.Should().BeTrue();
        release.Version.Should().Be("610.47");
        release.ReleaseDate.Should().Be(new DateOnly(2026, 5, 26));
        release.DownloadUrl.Host.Should().Be("us.download.nvidia.com");
        release.SizeBytes.Should().BeInRange(900_000_000, 1_100_000_000);
    }

    [Fact]
    public void TryParseLatestRelease_returns_false_for_empty_ids()
    {
        NvidiaGraphicsSource.TryParseLatestRelease("""{"IDS":[]}""", out _).Should().BeFalse();
    }

    [Fact]
    public void TryParseLatestRelease_returns_false_for_malformed_json()
    {
        NvidiaGraphicsSource.TryParseLatestRelease("not json at all", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("Tue May 26, 2026", 2026, 5, 26)]
    [InlineData("Fri Apr 4, 2025", 2025, 4, 4)]
    [InlineData("Wed Jan 15, 2025", 2025, 1, 15)]
    public void TryParseNvidiaDate_handles_short_weekday_prefix(string raw, int year, int month, int day)
    {
        var ok = NvidiaGraphicsSource.TryParseNvidiaDate(raw, out var date);

        ok.Should().BeTrue();
        date.Should().Be(new DateOnly(year, month, day));
    }

    [Fact]
    public void BuildApiUri_produces_windows_11_relative_uri_with_expected_params()
    {
        var uri = NvidiaGraphicsSource.BuildApiUri(isWindows11OrLater: true);

        uri.IsAbsoluteUri.Should().BeFalse();
        uri.OriginalString.Should().Contain("psid=131").And.Contain("pfid=1066").And.Contain("osID=135").And.Contain("dch=1").And.Contain("isWHQL=1");
    }

    [Fact]
    public void BuildApiUri_uses_windows_10_id_for_windows_10()
    {
        NvidiaGraphicsSource.BuildApiUri(isWindows11OrLater: false)
            .OriginalString.Should().Contain("osID=57");
    }

    [Theory]
    [InlineData(Architecture.X64, true)]
    [InlineData(Architecture.X86, false)]
    [InlineData(Architecture.Arm64, false)]
    [InlineData(Architecture.Arm, false)]
    public void SupportsArchitecture_only_accepts_x64(Architecture architecture, bool expected)
    {
        NvidiaGraphicsSource.SupportsArchitecture(architecture).Should().Be(expected);
    }

    private static NvidiaGraphicsSource NewSource(string json)
    {
        var client = new HttpClient(new StaticJsonHandler(json)) { BaseAddress = new Uri("https://gfwsl.geforce.com/") };
        return new NvidiaGraphicsSource(client, NullLogger<NvidiaGraphicsSource>.Instance);
    }

    private static DriverInfo NewNvidiaDriver(string deviceName, DateOnly currentDate) => new(
        DeviceId: "PCI\\VEN_10DE&DEV_2704",
        HardwareId: "PCI\\VEN_10DE&DEV_2704",
        DeviceName: deviceName,
        Category: DriverCategory.Display,
        Provider: "NVIDIA",
        Manufacturer: "NVIDIA",
        CurrentVersion: new Version(560, 0, 0, 0),
        CurrentDate: currentDate,
        InfName: "nv_dispi.inf",
        InfPath: null,
        IsSigned: true,
        DeviceClass: "DISPLAY");

    private sealed class StaticJsonHandler : HttpMessageHandler
    {
        private readonly string _json;
        public StaticJsonHandler(string json) { _json = json; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_json) });
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("simulated network failure");
    }
}
