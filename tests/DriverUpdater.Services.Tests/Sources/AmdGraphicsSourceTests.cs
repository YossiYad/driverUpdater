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
    public async Task SearchAsync_skips_when_same_adrenalin_package_is_already_installed()
    {
        var source = NewSource("""
            <p>Revision Number</p><p>Adrenalin 26.6.2 (WHQL Recommended)</p>
            <p>Release Date</p><p>2026-06-22</p>
            <a href="https://drivers.amd.com/drivers/whql-amd-software-adrenalin-edition-26.6.2-win11-b.exe">Download</a>
            """, installedVersion: "26.6.2");

        var results = await source.SearchAsync(new[] { NewAmdDriver(new DateOnly(2026, 2, 17)) }).ToListAsync();

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
    public void TryParseLatestWindowsRelease_prefers_full_installer_over_web_stub_when_both_present()
    {
        // When AMD lists both options on the same support page, pick the full installer
        // so the silent-install path actually runs end-to-end. The stub URL comes first
        // in the HTML, mirroring how amd.com tends to lay out the "Download" sections.
        var ok = AmdGraphicsSource.TryParseLatestWindowsRelease("""
            <p>Revision Number</p><p>Adrenalin 26.5.2 (WHQL Recommended)</p>
            <p>Release Date</p><p>2026-05-14</p>
            <a href="https://drivers.amd.com/drivers/whql-amd-software-adrenalin-edition-26.5.2-minimalsetup-260514_web.exe">Minimal Setup</a>
            <a href="https://drivers.amd.com/drivers/whql-amd-software-adrenalin-edition-26.5.2-fullinstall-260514.exe">Full Install</a>
            """, out var release);

        ok.Should().BeTrue();
        release.DirectInstallerUrl!.AbsoluteUri.Should().Be(
            "https://drivers.amd.com/drivers/whql-amd-software-adrenalin-edition-26.5.2-fullinstall-260514.exe");
    }

    [Fact]
    public void TryParseLatestWindowsRelease_falls_back_to_stub_when_only_stub_available()
    {
        var ok = AmdGraphicsSource.TryParseLatestWindowsRelease("""
            <p>Revision Number</p><p>Adrenalin 26.5.2 (WHQL Recommended)</p>
            <p>Release Date</p><p>2026-05-14</p>
            <a href="https://drivers.amd.com/drivers/whql-amd-software-adrenalin-edition-26.5.2-minimalsetup-260514_web.exe">Download</a>
            """, out var release);

        ok.Should().BeTrue();
        release.DirectInstallerUrl!.AbsoluteUri.Should().EndWith("_web.exe");
    }

    [Fact]
    public async Task SearchAsync_demotes_minimalsetup_web_stub_to_vendor_page()
    {
        // The AMD download links are typically labeled "Minimal Setup" / *_web.exe -
        // a tiny downloader stub that opens its own GUI and ignores /S. Demoting it
        // means we open the support page in the browser instead of trying to silent
        // install something that will never silent install.
        var source = NewSource("""
            <p>Revision Number</p><p>Adrenalin 26.5.2 (WHQL Recommended)</p>
            <p>File Size</p><p>818 MB</p>
            <p>Release Date</p><p>2026-05-14</p>
            <a href="https://drivers.amd.com/drivers/whql-amd-software-adrenalin-edition-26.5.2-minimalsetup-260514_web.exe">Download</a>
            """);
        var driver = NewAmdDriver(new DateOnly(2026, 2, 17));

        var results = await source.SearchAsync(new[] { driver }).ToListAsync();

        results.Should().ContainSingle();
        results[0].InstallKind.Should().Be(UpdateInstallKind.VendorPage);
        results[0].DownloadUrl.AbsoluteUri.Should().Contain("amd.com");
        results[0].DownloadUrl.AbsoluteUri.Should().NotContain("_web.exe");
    }

    [Fact]
    public async Task SearchAsync_returns_vendor_installer_for_non_web_direct_url()
    {
        var source = NewSource("""
            <p>Revision Number</p><p>Adrenalin 26.5.2 (WHQL Recommended)</p>
            <p>File Size</p><p>818 MB</p>
            <p>Release Date</p><p>2026-05-14</p>
            <a href="https://drivers.amd.com/drivers/whql-amd-software-adrenalin-edition-26.5.2-fullinstall-260514.exe">Download</a>
            """);
        var driver = NewAmdDriver(new DateOnly(2026, 2, 17));

        var results = await source.SearchAsync(new[] { driver }).ToListAsync();

        results.Should().ContainSingle();
        results[0].InstallKind.Should().Be(UpdateInstallKind.VendorInstaller);
        results[0].SourceUpdateId.Should().StartWith("vendor-installer:nullsoft:amd-radeon:");
        results[0].DownloadUrl.AbsoluteUri.Should().Be("https://drivers.amd.com/drivers/whql-amd-software-adrenalin-edition-26.5.2-fullinstall-260514.exe");
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
        // The scraped href is a *_web.exe stub which we now demote to VendorPage
        // instead of attempting a silent install that always opens the AMD GUI.
        results[0].InstallKind.Should().Be(UpdateInstallKind.VendorPage);
        results[0].DownloadUrl.AbsoluteUri.Should().Contain("amd.com");
    }

    [Fact]
    public void TryResolveSupportPage_returns_generic_url_for_non_rx_devices()
    {
        var igpu = NewAmdDriver(new DateOnly(2026, 2, 17)) with { DeviceName = "AMD Radeon(TM) Graphics" };

        var ok = AmdGraphicsSource.TryResolveSupportPage(igpu, out var uri);

        ok.Should().BeTrue();
        uri.AbsoluteUri.Should().Be(AmdGraphicsSource.AmdSupportUrl);
    }

    private static AmdGraphicsSource NewSource(string html, string? installedVersion = null)
    {
        var client = new HttpClient(new StaticHtmlHandler(html))
        {
            BaseAddress = new Uri("https://www.amd.com/")
        };
        return new AmdGraphicsSource(
            client,
            NullLogger<AmdGraphicsSource>.Instance,
            () => installedVersion);
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
