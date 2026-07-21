using System.Net;
using DriverUpdater.Core.Models;
using DriverUpdater.Services.Sources;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriverUpdater.Services.Tests.Sources;

public class OfficialVendorPageSourceTests
{
    [Theory]
    [InlineData("NVIDIA GeForce RTX 4080", "NVIDIA", DriverCategory.Display, "nvidia.com")]
    [InlineData("Intel Wi-Fi 7 BE200", "Intel", DriverCategory.Network, "intel.com")]
    [InlineData("Realtek PCIe 2.5GbE Family Controller", "Realtek", DriverCategory.Network, "realtek.com")]
    [InlineData("LIGHTSPEED Receiver", "Logitech", DriverCategory.Usb, "support.logi.com")]
    public void TryResolveVendorPage_maps_common_vendor_devices(string deviceName, string provider, DriverCategory category, string expectedHost)
    {
        var ok = OfficialVendorPageSource.TryResolveVendorPage(NewDriver(deviceName, provider, category, new DateOnly(2025, 1, 1)), out _, out var page);

        ok.Should().BeTrue();
        page.Host.Should().Contain(expectedHost);
    }

    [Fact]
    public async Task SearchAsync_yields_vendor_page_only_for_old_vendor_driver()
    {
        var source = new OfficialVendorPageSource(
            NullLogger<OfficialVendorPageSource>.Instance,
            new FakeTimeProvider(new DateTimeOffset(2026, 5, 28, 0, 0, 0, TimeSpan.Zero)));

        var results = await source.SearchAsync(new[]
        {
            NewDriver("Realtek PCIe 2.5GbE Family Controller", "Realtek", DriverCategory.Network, new DateOnly(2024, 1, 1)),
            NewDriver("Realtek Audio (HD)", "Realtek", DriverCategory.Audio, new DateOnly(2026, 5, 1))
        }).ToListAsync();

        results.Should().ContainSingle();
        results[0].InstallKind.Should().Be(UpdateInstallKind.VendorPage);
        results[0].DownloadUrl.Host.Should().Contain("realtek.com");
    }

    [Fact]
    public async Task SearchAsync_yields_vendor_installer_when_vendor_page_has_direct_msi()
    {
        var source = new OfficialVendorPageSource(
            NullLogger<OfficialVendorPageSource>.Instance,
            new FakeTimeProvider(new DateTimeOffset(2026, 5, 28, 0, 0, 0, TimeSpan.Zero)),
            httpClient: new HttpClient(new StaticHtmlHandler("""
                <html><body>
                  <a href="https://downloads.realtek.com/realtek-driver-1.2.3.msi">Download driver</a>
                </body></html>
                """)));

        var results = await source.SearchAsync(new[]
        {
            NewDriver("Realtek PCIe 2.5GbE Family Controller", "Realtek", DriverCategory.Network, new DateOnly(2024, 1, 1))
        }).ToListAsync();

        results.Should().ContainSingle();
        results[0].InstallKind.Should().Be(UpdateInstallKind.VendorInstaller);
        results[0].Confidence.Should().Be(UpdateConfidence.Confirmed);
        results[0].SourceUpdateId.Should().StartWith("vendor-installer:msi-wrapper:Realtek:");
        results[0].DownloadUrl.AbsoluteUri.Should().Be("https://downloads.realtek.com/realtek-driver-1.2.3.msi");
    }

    [Fact]
    public void TryFindAppInstallablePackage_rejects_cross_site_downloads()
    {
        var ok = OfficialVendorPageSource.TryFindAppInstallablePackage(
            new Uri("https://vendor.example.com/support/device"),
            """<a href="https://unrelated.example.net/driver-pack.zip">Driver package</a>""",
            out _,
            out _);

        ok.Should().BeFalse();
    }

    [Fact]
    public void TryFindAppInstallablePackage_resolves_relative_zip_links()
    {
        var ok = OfficialVendorPageSource.TryFindAppInstallablePackage(
            new Uri("https://vendor.example.com/support/device"),
            """<a href="/downloads/driver-pack.zip">Driver package</a>""",
            out var packageUrl,
            out var installerKind);

        ok.Should().BeTrue();
        packageUrl.AbsoluteUri.Should().Be("https://vendor.example.com/downloads/driver-pack.zip");
        installerKind.Should().Be("zip-inf");
    }

    [Fact]
    public async Task SearchAsync_offers_display_drivers_after_short_period()
    {
        var source = new OfficialVendorPageSource(
            NullLogger<OfficialVendorPageSource>.Instance,
            new FakeTimeProvider(new DateTimeOffset(2026, 5, 28, 0, 0, 0, TimeSpan.Zero)));

        var results = await source.SearchAsync(new[]
        {
            NewDriver("NVIDIA GeForce RTX 4080", "NVIDIA", DriverCategory.Display, new DateOnly(2026, 5, 1)),
            NewDriver("AMD Radeon RX 6700 XT", "Advanced Micro Devices, Inc.", DriverCategory.Display, new DateOnly(2026, 5, 10))
        }).ToListAsync();

        results.Should().HaveCount(2);
        results.Should().Contain(r => r.DownloadUrl.Host.Contains("nvidia.com"));
        results.Should().Contain(r => r.DownloadUrl.Host.Contains("amd.com"));
    }

    [Fact]
    public async Task SearchAsync_skips_display_driver_installed_within_two_weeks()
    {
        var source = new OfficialVendorPageSource(
            NullLogger<OfficialVendorPageSource>.Instance,
            new FakeTimeProvider(new DateTimeOffset(2026, 5, 28, 0, 0, 0, TimeSpan.Zero)));

        var results = await source.SearchAsync(new[]
        {
            NewDriver("NVIDIA GeForce RTX 4080", "NVIDIA", DriverCategory.Display, new DateOnly(2026, 5, 20))
        }).ToListAsync();

        results.Should().BeEmpty();
    }

    [Theory]
    [InlineData("AMD Radeon RX 580", "Advanced Micro Devices, Inc.")]
    [InlineData("AMD Radeon Vega 8 Graphics", "AMD")]
    [InlineData("Intel UHD Graphics 630", "Intel Corporation")]
    [InlineData("Intel Iris Xe Graphics", "Intel Corporation")]
    public void TryResolveVendorPage_maps_additional_display_gpus(string deviceName, string provider)
    {
        var ok = OfficialVendorPageSource.TryResolveVendorPage(
            NewDriver(deviceName, provider, DriverCategory.Display, new DateOnly(2025, 1, 1)),
            out _,
            out var page);

        ok.Should().BeTrue();
        page.Should().NotBeNull();
    }

    [Fact]
    public async Task SearchAsync_skips_logitech_when_g_hub_is_installed()
    {
        var source = new OfficialVendorPageSource(
            NullLogger<OfficialVendorPageSource>.Instance,
            new FakeTimeProvider(new DateTimeOffset(2026, 5, 28, 0, 0, 0, TimeSpan.Zero)),
            path => path.EndsWith("lghub.exe", StringComparison.OrdinalIgnoreCase));

        var results = await source.SearchAsync(new[]
        {
            NewDriver("LIGHTSPEED Receiver", "Logitech", DriverCategory.Usb, new DateOnly(2024, 1, 1)),
            NewDriver("Realtek PCIe 2.5GbE Family Controller", "Realtek", DriverCategory.Network, new DateOnly(2024, 1, 1))
        }).ToListAsync();

        results.Should().ContainSingle();
        results[0].DownloadUrl.Host.Should().Contain("realtek.com");
    }

    [Fact]
    public async Task SearchAsync_offers_logitech_page_when_g_hub_not_installed()
    {
        var source = new OfficialVendorPageSource(
            NullLogger<OfficialVendorPageSource>.Instance,
            new FakeTimeProvider(new DateTimeOffset(2026, 5, 28, 0, 0, 0, TimeSpan.Zero)),
            _ => false);

        var results = await source.SearchAsync(new[]
        {
            NewDriver("LIGHTSPEED Receiver", "Logitech", DriverCategory.Usb, new DateOnly(2024, 1, 1))
        }).ToListAsync();

        results.Should().ContainSingle();
        results[0].DownloadUrl.Host.Should().Contain("logi.com");
    }

    private static DriverInfo NewDriver(string deviceName, string provider, DriverCategory category, DateOnly currentDate) => new(
        DeviceId: $"ID\\{deviceName}",
        HardwareId: $"HW\\{deviceName}",
        DeviceName: deviceName,
        Category: category,
        Provider: provider,
        Manufacturer: provider,
        CurrentVersion: new Version(1, 0, 0, 0),
        CurrentDate: currentDate,
        InfName: "oem.inf",
        InfPath: null,
        IsSigned: true,
        DeviceClass: category.ToString());

    private sealed class FakeTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FakeTimeProvider(DateTimeOffset now)
        {
            _now = now;
        }

        public override DateTimeOffset GetUtcNow() => _now;
    }

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
