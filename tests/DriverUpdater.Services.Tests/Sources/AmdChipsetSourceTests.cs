using System.Net;
using DriverUpdater.Core.Models;
using DriverUpdater.Services.Sources;
using DriverUpdater.Services.Sources.Internal;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriverUpdater.Services.Tests.Sources;

public class AmdChipsetSourceTests
{
    private const string SampleB850Html = """
        <html><body>
          <div>
            <strong>Revision Number</strong>
            <p>8.05.04.516</p>
          </div>
          <div>
            <strong>File Size</strong>
            <p>34 MB</p>
          </div>
          <div>
            <strong>Release Date</strong>
            <p>2026-05-18</p>
          </div>
          <a href="https://drivers.amd.com/drivers/amd_chipset_software_8.05.04.516.exe">Download</a>
        </body></html>
        """;

    [Fact]
    public async Task SearchAsync_yields_vendor_installer_when_direct_url_present()
    {
        var source = NewSource(SampleB850Html, ("am5", "b850"));
        var driver = NewAmdChipsetDriver("AMD I2C Controller", new DateOnly(2025, 9, 9));

        var results = await source.SearchAsync(new[] { driver }).ToListAsync();

        results.Should().ContainSingle();
        results[0].InstallKind.Should().Be(UpdateInstallKind.VendorInstaller);
        results[0].SourceUpdateId.Should().Be("vendor-installer:nullsoft:amd-chipset:8.05.04.516");
        results[0].DownloadUrl.AbsoluteUri.Should().Be("https://drivers.amd.com/drivers/amd_chipset_software_8.05.04.516.exe");
        results[0].NewDate.Should().Be(new DateOnly(2026, 5, 18));
    }

    [Fact]
    public async Task SearchAsync_falls_back_to_vendor_page_when_parser_fails()
    {
        var source = NewSource("<html><body>nothing parseable here</body></html>", ("am5", "b850"));
        var driver = NewAmdChipsetDriver("AMD Provisioning Packages", new DateOnly(2025, 11, 17));

        var results = await source.SearchAsync(new[] { driver }).ToListAsync();

        results.Should().ContainSingle();
        results[0].InstallKind.Should().Be(UpdateInstallKind.VendorPage);
        results[0].Confidence.Should().Be(UpdateConfidence.Advisory);
        results[0].DownloadUrl.AbsoluteUri.Should().Be("https://www.amd.com/en/support/chipsets");
    }

    [Fact]
    public async Task SearchAsync_skips_when_local_driver_already_newer_than_release()
    {
        var source = NewSource(SampleB850Html, ("am5", "b850"));
        var driver = NewAmdChipsetDriver("AMD I2C Controller", new DateOnly(2026, 6, 1));

        var results = await source.SearchAsync(new[] { driver }).ToListAsync();

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchAsync_skips_amd_display_drivers()
    {
        var source = NewSource(SampleB850Html, ("am5", "b850"));
        var displayDriver = new DriverInfo(
            DeviceId: "PCI\\VEN_1002&DEV_747E",
            HardwareId: "PCI\\VEN_1002&DEV_747E",
            DeviceName: "AMD Radeon RX 7700 XT",
            Category: DriverCategory.Display,
            Provider: "Advanced Micro Devices, Inc.",
            Manufacturer: "Advanced Micro Devices, Inc.",
            CurrentVersion: new Version(32, 0, 23027, 2005),
            CurrentDate: new DateOnly(2026, 2, 17),
            InfName: "oem69.inf",
            InfPath: null,
            IsSigned: true,
            DeviceClass: "DISPLAY");

        var results = await source.SearchAsync(new[] { displayDriver }).ToListAsync();

        results.Should().BeEmpty();
    }

    [Fact]
    public void IsSupportedAmdChipsetDriver_matches_amd_chipset_and_system_categories()
    {
        var chipset = NewAmdChipsetDriver("AMD I2C Controller", new DateOnly(2026, 1, 1));
        var system = chipset with { Category = DriverCategory.System, DeviceName = "AMD-Vulkan User Mode Driver" };
        var display = chipset with { Category = DriverCategory.Display, DeviceName = "AMD Radeon RX 7700 XT" };
        var nonAmd = chipset with { Provider = "Intel Corporation", Manufacturer = "Intel", DeviceName = "Intel I2C Controller" };
        var virt = chipset with { DeviceName = "AMD Virtual Bus" };

        AmdChipsetSource.IsSupportedAmdChipsetDriver(chipset).Should().BeTrue();
        AmdChipsetSource.IsSupportedAmdChipsetDriver(system).Should().BeTrue();
        AmdChipsetSource.IsSupportedAmdChipsetDriver(display).Should().BeFalse();
        AmdChipsetSource.IsSupportedAmdChipsetDriver(nonAmd).Should().BeFalse();
        AmdChipsetSource.IsSupportedAmdChipsetDriver(virt).Should().BeFalse();
    }

    [Fact]
    public void TryParseLatestRelease_reads_version_date_size_and_installer_url()
    {
        var ok = AmdChipsetSource.TryParseLatestRelease(SampleB850Html, out var release);

        ok.Should().BeTrue();
        release.Version.Should().Be("8.05.04.516");
        release.ReleaseDate.Should().Be(new DateOnly(2026, 5, 18));
        release.SizeBytes.Should().Be(35651584);
        release.DirectInstallerUrl!.AbsoluteUri.Should().Be("https://drivers.amd.com/drivers/amd_chipset_software_8.05.04.516.exe");
    }

    private static AmdChipsetSource NewSource(string html, (string Socket, string Slug) detected)
    {
        var client = new HttpClient(new StaticHtmlHandler(html))
        {
            BaseAddress = new Uri("https://www.amd.com/")
        };
        var detector = new StubSocketDetector(new AmdSocketInfo(detected.Socket, detected.Slug, IsFallback: false));
        return new AmdChipsetSource(client, detector, NullLogger<AmdChipsetSource>.Instance);
    }

    private static DriverInfo NewAmdChipsetDriver(string deviceName, DateOnly currentDate) => new(
        DeviceId: "PCI\\VEN_1022&DEV_15E2",
        HardwareId: $"PCI\\VEN_1022&DEV_15E2\\{deviceName.GetHashCode()}",
        DeviceName: deviceName,
        Category: DriverCategory.Chipset,
        Provider: "Advanced Micro Devices, Inc.",
        Manufacturer: "Advanced Micro Devices, Inc.",
        CurrentVersion: new Version(1, 2, 0, 126),
        CurrentDate: currentDate,
        InfName: "oem42.inf",
        InfPath: null,
        IsSigned: true,
        DeviceClass: "SYSTEM");

    private sealed class StaticHtmlHandler : HttpMessageHandler
    {
        private readonly string _html;
        public StaticHtmlHandler(string html) { _html = html; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_html) });
    }

    private sealed class StubSocketDetector : IAmdSocketDetector
    {
        private readonly AmdSocketInfo _info;
        public StubSocketDetector(AmdSocketInfo info) { _info = info; }
        public Task<AmdSocketInfo> DetectAsync(CancellationToken cancellationToken = default) => Task.FromResult(_info);
    }
}
