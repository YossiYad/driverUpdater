using System.Net;
using DriverUpdater.Core.Models;
using DriverUpdater.Services.Sources;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriverUpdater.Services.Tests.Sources;

public class AmdChipsetSourceTests
{
    [Fact]
    public async Task SearchAsync_yields_vendor_installer_candidate_for_amd_chipset_driver()
    {
        var source = NewSource("""
            <p>AMD Chipset Drivers 7.04.09.545</p>
            <p>File Size</p><p>34 MB</p>
            <p>Release Date</p><p>2026-04-22</p>
            <a href="https://drivers.amd.com/drivers/amd_chipset_software_7.04.09.545.exe">Download</a>
            """);
        var driver = NewAmdChipsetDriver("AMD I2C Controller", new DateOnly(2025, 9, 9));

        var results = await source.SearchAsync(new[] { driver }).ToListAsync();

        results.Should().ContainSingle();
        results[0].InstallKind.Should().Be(UpdateInstallKind.VendorInstaller);
        results[0].SourceUpdateId.Should().StartWith("vendor-installer:installshield:amd-chipset:");
        results[0].DownloadUrl.AbsoluteUri.Should().Be("https://drivers.amd.com/drivers/amd_chipset_software_7.04.09.545.exe");
    }

    [Fact]
    public async Task SearchAsync_falls_back_to_vendor_page_when_no_direct_url()
    {
        var source = NewSource("""
            <p>AMD Chipset Driver 7.04.09.545</p>
            <p>Release Date</p><p>2026-04-22</p>
            """);
        var driver = NewAmdChipsetDriver("AMD Provisioning Packages", new DateOnly(2025, 11, 17));

        var results = await source.SearchAsync(new[] { driver }).ToListAsync();

        results.Should().ContainSingle();
        results[0].InstallKind.Should().Be(UpdateInstallKind.VendorPage);
    }

    [Fact]
    public async Task SearchAsync_skips_when_local_driver_already_newer()
    {
        var source = NewSource("""
            <p>AMD Chipset Drivers 7.04.09.545</p>
            <p>Release Date</p><p>2026-04-22</p>
            """);
        var driver = NewAmdChipsetDriver("AMD I2C Controller", new DateOnly(2026, 5, 1));

        var results = await source.SearchAsync(new[] { driver }).ToListAsync();

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchAsync_skips_amd_display_drivers()
    {
        var source = NewSource("""
            <p>AMD Chipset Drivers 7.04.09.545</p>
            <p>Release Date</p><p>2026-04-22</p>
            """);
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

        AmdChipsetSource.IsSupportedAmdChipsetDriver(chipset).Should().BeTrue();
        AmdChipsetSource.IsSupportedAmdChipsetDriver(system).Should().BeTrue();
        AmdChipsetSource.IsSupportedAmdChipsetDriver(display).Should().BeFalse();
        AmdChipsetSource.IsSupportedAmdChipsetDriver(nonAmd).Should().BeFalse();
    }

    [Fact]
    public void TryParseLatestRelease_reads_version_date_and_size()
    {
        var ok = AmdChipsetSource.TryParseLatestRelease("""
            <p>AMD Chipset Drivers 7.04.09.545</p>
            <p>File Size</p><p>34 MB</p>
            <p>Release Date</p><p>2026-04-22</p>
            <a href="https://drivers.amd.com/drivers/amd_chipset_software_7.04.09.545.exe">Download</a>
            """, out var release);

        ok.Should().BeTrue();
        release.Version.Should().Be("7.04.09.545");
        release.ReleaseDate.Should().Be(new DateOnly(2026, 4, 22));
        release.SizeBytes.Should().Be(35651584);
        release.DirectInstallerUrl.Should().NotBeNull();
    }

    private static AmdChipsetSource NewSource(string html)
    {
        var client = new HttpClient(new StaticHtmlHandler(html))
        {
            BaseAddress = new Uri("https://www.amd.com/")
        };
        return new AmdChipsetSource(client, NullLogger<AmdChipsetSource>.Instance);
    }

    private static DriverInfo NewAmdChipsetDriver(string deviceName, DateOnly currentDate) => new(
        DeviceId: "PCI\\VEN_1022&DEV_15E2",
        HardwareId: "PCI\\VEN_1022&DEV_15E2",
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

        public StaticHtmlHandler(string html)
        {
            _html = html;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_html)
            });
    }
}
