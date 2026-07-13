using System.Net;
using DriverUpdater.Core.Models;
using DriverUpdater.Core.Options;
using DriverUpdater.Services.Install;
using DriverUpdater.Services.Web;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DriverUpdater.Services.Tests.Install;

public class VendorPageInstallerResolverTests
{
    [Fact]
    public void TryFindInstallerLink_prefers_msi_over_zip()
    {
        const string html = """
            <a href="/downloads/tool.zip">zip</a>
            <a href="/downloads/driver.msi">msi</a>
            """;

        var ok = VendorPageInstallerResolver.TryFindInstallerLink(
            new Uri("https://vendor.example.com/support/page.html"), html, out var url, out var kind);

        ok.Should().BeTrue();
        url.Should().Be(new Uri("https://vendor.example.com/downloads/driver.msi"));
        kind.Should().Be("msi-wrapper");
    }

    [Fact]
    public void TryFindInstallerLink_resolves_relative_urls_against_page()
    {
        const string html = "<a href='files/pkg.zip'>download</a>";

        var ok = VendorPageInstallerResolver.TryFindInstallerLink(
            new Uri("https://vendor.example.com/support/board/"), html, out var url, out var kind);

        ok.Should().BeTrue();
        url.Should().Be(new Uri("https://vendor.example.com/support/board/files/pkg.zip"));
        kind.Should().Be("zip-inf");
    }

    [Fact]
    public void TryFindInstallerLink_accepts_nvidia_exe()
    {
        const string html = "<a href=\"https://us.download.nvidia.com/Windows/576.02/576.02-desktop-win10-win11-64bit-international-dch-whql.exe\">GRD</a>";

        var ok = VendorPageInstallerResolver.TryFindInstallerLink(
            new Uri("https://www.nvidia.com/Download/index.aspx"), html, out var url, out var kind);

        ok.Should().BeTrue();
        url.Host.Should().Be("us.download.nvidia.com");
        kind.Should().Be("nvidia");
    }

    [Fact]
    public void TryFindInstallerLink_accepts_amd_chipset_exe()
    {
        const string html = "<a href=\"https://drivers.amd.com/drivers/amd_chipset_software_7.03.11.361.exe\">chipset</a>";

        var ok = VendorPageInstallerResolver.TryFindInstallerLink(
            new Uri("https://www.amd.com/en/support/chipsets"), html, out _, out var kind);

        ok.Should().BeTrue();
        kind.Should().Be("amd-chipset");
    }

    [Fact]
    public void TryFindInstallerLink_rejects_unknown_exe()
    {
        const string html = "<a href=\"https://vendor.example.com/setup.exe\">setup</a>";

        var ok = VendorPageInstallerResolver.TryFindInstallerLink(
            new Uri("https://vendor.example.com/support.html"), html, out _, out _);

        ok.Should().BeFalse();
    }

    [Fact]
    public void TryClassifyExe_rejects_amd_adrenalin_installers()
    {
        var ok = VendorPageInstallerResolver.TryClassifyExe(
            new Uri("https://drivers.amd.com/drivers/whql-amd-software-adrenalin-edition-25.5.1-win10-win11-may2025.exe"), out _);

        ok.Should().BeFalse();
    }

    [Fact]
    public async Task TryResolveAsync_rewrites_candidate_to_vendor_installer()
    {
        const string html = "<a href=\"/downloads/driver.msi\">download</a>";
        var resolver = new VendorPageInstallerResolver(
            new StubHttpClientFactory(html),
            NullLogger<VendorPageInstallerResolver>.Instance);

        var resolved = await resolver.TryResolveAsync(NewVendorPageCandidate());

        resolved.Should().NotBeNull();
        resolved!.InstallKind.Should().Be(UpdateInstallKind.VendorInstaller);
        resolved.DownloadUrl.Should().Be(new Uri("https://vendor.example.com/downloads/driver.msi"));
        resolved.SourceUpdateId.Should().Be("vendor-installer:msi-wrapper:resolved:vendor-page:Test:PCI\\X");
    }

    [Fact]
    public async Task TryResolveAsync_returns_null_when_page_has_no_installer()
    {
        var resolver = new VendorPageInstallerResolver(
            new StubHttpClientFactory("<html>nothing here</html>"),
            NullLogger<VendorPageInstallerResolver>.Instance);

        var resolved = await resolver.TryResolveAsync(NewVendorPageCandidate());

        resolved.Should().BeNull();
    }

    [Fact]
    public async Task TryResolveAsync_returns_null_when_fetch_fails()
    {
        var resolver = new VendorPageInstallerResolver(
            new StubHttpClientFactory(null),
            NullLogger<VendorPageInstallerResolver>.Instance);

        var resolved = await resolver.TryResolveAsync(NewVendorPageCandidate());

        resolved.Should().BeNull();
    }

    [Fact]
    public async Task TryResolveAsync_ignores_non_vendor_page_candidates()
    {
        var resolver = new VendorPageInstallerResolver(
            new StubHttpClientFactory("<a href='/x.msi'>x</a>"),
            NullLogger<VendorPageInstallerResolver>.Instance);

        var resolved = await resolver.TryResolveAsync(
            NewVendorPageCandidate() with { InstallKind = UpdateInstallKind.WindowsUpdate });

        resolved.Should().BeNull();
    }

    [Fact]
    public async Task TryResolveAsync_falls_back_to_browser_fetch_when_http_is_blocked()
    {
        var browserFetcher = new StubBrowserFetcher("<a href=\"/downloads/driver.msi\">download</a>");
        var resolver = new VendorPageInstallerResolver(
            new StubHttpClientFactory(null),
            NullLogger<VendorPageInstallerResolver>.Instance,
            new Lazy<IBrowserHtmlFetcher>(() => browserFetcher),
            new StubScraperSettings(enablePlaywrightFallback: true));

        var resolved = await resolver.TryResolveAsync(NewVendorPageCandidate());

        browserFetcher.WasCalled.Should().BeTrue();
        resolved.Should().NotBeNull();
        resolved!.InstallKind.Should().Be(UpdateInstallKind.VendorInstaller);
        resolved.DownloadUrl.Should().Be(new Uri("https://vendor.example.com/downloads/driver.msi"));
    }

    [Fact]
    public async Task TryResolveAsync_skips_browser_fallback_when_disabled()
    {
        var browserFetcher = new StubBrowserFetcher("<a href=\"/downloads/driver.msi\">download</a>");
        var resolver = new VendorPageInstallerResolver(
            new StubHttpClientFactory(null),
            NullLogger<VendorPageInstallerResolver>.Instance,
            new Lazy<IBrowserHtmlFetcher>(() => browserFetcher),
            new StubScraperSettings(enablePlaywrightFallback: false));

        var resolved = await resolver.TryResolveAsync(NewVendorPageCandidate());

        browserFetcher.WasCalled.Should().BeFalse();
        resolved.Should().BeNull();
    }

    [Fact]
    public async Task TryResolveAsync_does_not_use_browser_when_http_succeeds()
    {
        var browserFetcher = new StubBrowserFetcher("<a href=\"/other.msi\">x</a>");
        var resolver = new VendorPageInstallerResolver(
            new StubHttpClientFactory("<a href=\"/downloads/driver.msi\">download</a>"),
            NullLogger<VendorPageInstallerResolver>.Instance,
            new Lazy<IBrowserHtmlFetcher>(() => browserFetcher),
            new StubScraperSettings(enablePlaywrightFallback: true));

        var resolved = await resolver.TryResolveAsync(NewVendorPageCandidate());

        browserFetcher.WasCalled.Should().BeFalse();
        resolved!.DownloadUrl.Should().Be(new Uri("https://vendor.example.com/downloads/driver.msi"));
    }

    private sealed class StubBrowserFetcher : IBrowserHtmlFetcher
    {
        private readonly string? _html;

        public StubBrowserFetcher(string? html)
        {
            _html = html;
        }

        public bool WasCalled { get; private set; }

        public Task<string?> TryFetchHtmlAsync(Uri url, CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            return Task.FromResult(_html);
        }
    }

    private sealed class StubScraperSettings : IOptionsMonitor<ScraperSettings>
    {
        public StubScraperSettings(bool enablePlaywrightFallback)
        {
            CurrentValue = new ScraperSettings { EnablePlaywrightFallback = enablePlaywrightFallback };
        }

        public ScraperSettings CurrentValue { get; }

        public ScraperSettings Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<ScraperSettings, string?> listener) => null;
    }

    private static UpdateCandidate NewVendorPageCandidate() => new(
        ForHardwareId: "PCI\\X",
        Source: UpdateSource.Oem,
        NewVersion: new Version(2026, 7, 1, 0),
        NewDate: new DateOnly(2026, 7, 1),
        DownloadUrl: new Uri("https://vendor.example.com/support/page.html"),
        SizeBytes: 0,
        KbArticle: null,
        IsSuperseded: false,
        SourceUpdateId: "vendor-page:Test:PCI\\X",
        SupersededIds: Array.Empty<string>(),
        InstallKind: UpdateInstallKind.VendorPage,
        Confidence: UpdateConfidence.Advisory);

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly string? _html;

        public StubHttpClientFactory(string? html)
        {
            _html = html;
        }

        public HttpClient CreateClient(string name) => new(new Handler(_html));

        private sealed class Handler : HttpMessageHandler
        {
            private readonly string? _html;

            public Handler(string? html)
            {
                _html = html;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (_html is null)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(_html)
                });
            }
        }
    }
}
