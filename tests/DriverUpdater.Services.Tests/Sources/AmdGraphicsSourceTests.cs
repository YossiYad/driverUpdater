using System.Net;
using DriverUpdater.Core.Models;
using DriverUpdater.Services.Sources;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriverUpdater.Services.Tests.Sources;

public class AmdGraphicsSourceTests
{
    [Fact]
    public async Task SearchAsync_yields_oem_candidate_when_rx_7700_xt_driver_is_older_than_amd_release()
    {
        var source = NewSource("""
            <html>
              <body>
                <h4>AMD Software: Adrenalin Edition</h4>
                <p>Revision Number</p><p>Adrenalin 26.5.2 (WHQL Recommended)</p>
                <p>File Size</p><p>818 MB</p>
                <p>Release Date</p><p>2026-05-14</p>
              </body>
            </html>
            """);
        var driver = NewAmdDriver(new DateOnly(2026, 2, 17));

        var results = await source.SearchAsync(new[] { driver }).ToListAsync();

        results.Should().ContainSingle();
        results[0].Source.Should().Be(UpdateSource.Oem);
        results[0].ForHardwareId.Should().Be(driver.HardwareId);
        results[0].NewVersion.Should().Be(new Version(2026, 5, 14, 0));
        results[0].NewDate.Should().Be(new DateOnly(2026, 5, 14));
        results[0].DownloadUrl.AbsoluteUri.Should().Be("https://www.amd.com/en/support/downloads/drivers.html/graphics/radeon-rx/radeon-rx-7000-series/amd-radeon-rx-7700-xt.html");
    }

    [Fact]
    public async Task SearchAsync_skips_when_local_driver_date_is_current()
    {
        var source = NewSource("""
            <p>Revision Number</p><p>Adrenalin 26.5.2 (WHQL Recommended)</p>
            <p>Release Date</p><p>2026-05-14</p>
            """);

        var results = await source.SearchAsync(new[] { NewAmdDriver(new DateOnly(2026, 5, 14)) }).ToListAsync();

        results.Should().BeEmpty();
    }

    [Fact]
    public void TryResolveSupportPage_builds_model_specific_rx_urls()
    {
        var driver = NewAmdDriver(new DateOnly(2026, 2, 18)) with
        {
            DeviceName = "AMD Radeon RX 7900 XTX"
        };

        var ok = AmdGraphicsSource.TryResolveSupportPage(driver, out var supportUri);

        ok.Should().BeTrue();
        supportUri.AbsoluteUri.Should().Be("https://www.amd.com/en/support/downloads/drivers.html/graphics/radeon-rx/radeon-rx-7000-series/amd-radeon-rx-7900-xtx.html");
    }

    [Fact]
    public void TryParseLatestWindowsRelease_reads_revision_date_and_size()
    {
        var ok = AmdGraphicsSource.TryParseLatestWindowsRelease("""
            <p>Revision Number</p><p>Adrenalin 26.5.2 (WHQL Recommended)</p>
            <p>File Size</p><p>818 MB</p>
            <p>Release Date</p><p>2026-05-14</p>
            """, out var release);

        ok.Should().BeTrue();
        release.Revision.Should().Be("26.5.2");
        release.ReleaseDate.Should().Be(new DateOnly(2026, 5, 14));
        release.SizeBytes.Should().Be(857735168);
        release.DirectInstallerUrl.Should().BeNull();
    }

    [Fact]
    public void TryParseLatestWindowsRelease_extracts_direct_installer_url_when_present()
    {
        var ok = AmdGraphicsSource.TryParseLatestWindowsRelease("""
            <p>Revision Number</p><p>Adrenalin 26.5.2 (WHQL Recommended)</p>
            <p>Release Date</p><p>2026-05-14</p>
            <a href="https://drivers.amd.com/drivers/whql-amd-software-adrenalin-edition-26.5.2-minimalsetup-260514_web.exe">Download</a>
            """, out var release);

        ok.Should().BeTrue();
        release.DirectInstallerUrl.Should().NotBeNull();
        release.DirectInstallerUrl!.AbsoluteUri.Should().Be("https://drivers.amd.com/drivers/whql-amd-software-adrenalin-edition-26.5.2-minimalsetup-260514_web.exe");
    }

    [Fact]
    public async Task SearchAsync_returns_vendor_installer_candidate_when_direct_url_present()
    {
        var source = NewSource("""
            <p>Revision Number</p><p>Adrenalin 26.5.2 (WHQL Recommended)</p>
            <p>File Size</p><p>818 MB</p>
            <p>Release Date</p><p>2026-05-14</p>
            <a href="https://drivers.amd.com/drivers/whql-amd-software-adrenalin-edition-26.5.2-minimalsetup-260514_web.exe">Download</a>
            """);
        var driver = NewAmdDriver(new DateOnly(2026, 2, 17));

        var results = await source.SearchAsync(new[] { driver }).ToListAsync();

        results.Should().ContainSingle();
        results[0].InstallKind.Should().Be(UpdateInstallKind.VendorInstaller);
        results[0].SourceUpdateId.Should().StartWith("vendor-installer:installshield:amd-radeon:");
        results[0].DownloadUrl.AbsoluteUri.Should().Be("https://drivers.amd.com/drivers/whql-amd-software-adrenalin-edition-26.5.2-minimalsetup-260514_web.exe");
    }

    [Fact]
    public async Task SearchAsync_falls_back_to_vendor_page_when_direct_url_absent()
    {
        var source = NewSource("""
            <p>Revision Number</p><p>Adrenalin 26.5.2 (WHQL Recommended)</p>
            <p>File Size</p><p>818 MB</p>
            <p>Release Date</p><p>2026-05-14</p>
            """);
        var driver = NewAmdDriver(new DateOnly(2026, 2, 17));

        var results = await source.SearchAsync(new[] { driver }).ToListAsync();

        results.Should().ContainSingle();
        results[0].InstallKind.Should().Be(UpdateInstallKind.VendorPage);
    }

    [Fact]
    public async Task SearchAsync_yields_for_integrated_amd_radeon_graphics_via_generic_page()
    {
        var source = NewSource("""
            <p>Revision Number</p><p>Adrenalin 26.5.2 (WHQL Recommended)</p>
            <p>File Size</p><p>48 MB</p>
            <p>Release Date</p><p>2026-05-14</p>
            <a href="https://drivers.amd.com/drivers/installer/26.10/whql/amd-software-adrenalin-edition-26.5.2-minimalsetup-260513_web.exe">Download</a>
            """);
        var igpu = NewAmdDriver(new DateOnly(2026, 2, 17)) with
        {
            DeviceName = "AMD Radeon(TM) Graphics",
            HardwareId = "PCI\\VEN_1002&DEV_150E"
        };

        var results = await source.SearchAsync(new[] { igpu }).ToListAsync();

        results.Should().ContainSingle();
        results[0].InstallKind.Should().Be(UpdateInstallKind.VendorInstaller);
        results[0].DownloadUrl.AbsoluteUri.Should().Be("https://drivers.amd.com/drivers/installer/26.10/whql/amd-software-adrenalin-edition-26.5.2-minimalsetup-260513_web.exe");
    }

    [Fact]
    public void TryResolveSupportPage_returns_generic_url_for_non_rx_devices()
    {
        var igpu = NewAmdDriver(new DateOnly(2026, 2, 17)) with { DeviceName = "AMD Radeon(TM) Graphics" };

        var ok = AmdGraphicsSource.TryResolveSupportPage(igpu, out var uri);

        ok.Should().BeTrue();
        uri.AbsoluteUri.Should().Be(AmdGraphicsSource.AmdSupportUrl);
    }

    private static AmdGraphicsSource NewSource(string html)
    {
        var client = new HttpClient(new StaticHtmlHandler(html))
        {
            BaseAddress = new Uri("https://www.amd.com/")
        };
        return new AmdGraphicsSource(client, NullLogger<AmdGraphicsSource>.Instance);
    }

    private static DriverInfo NewAmdDriver(DateOnly currentDate) => new(
        DeviceId: "PCI\\VEN_1002&DEV_747E",
        HardwareId: "PCI\\VEN_1002&DEV_747E",
        DeviceName: "AMD Radeon RX 7700 XT",
        Category: DriverCategory.Display,
        Provider: "Advanced Micro Devices, Inc.",
        Manufacturer: "Advanced Micro Devices, Inc.",
        CurrentVersion: new Version(32, 0, 23027, 2005),
        CurrentDate: currentDate,
        InfName: "oem69.inf",
        InfPath: null,
        IsSigned: true,
        DeviceClass: "DISPLAY");

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
