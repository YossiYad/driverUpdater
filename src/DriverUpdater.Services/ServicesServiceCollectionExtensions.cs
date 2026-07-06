using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using DriverUpdater.Core.Options;
using DriverUpdater.Services.Ai;
using DriverUpdater.Services.Backup;
using DriverUpdater.Services.Install;
using DriverUpdater.Services.Scanning;
using DriverUpdater.Services.Sources;
using DriverUpdater.Services.Sources.Internal;
using DriverUpdater.Services.Sources.Internal.Motherboard;
using DriverUpdater.Services.Sources.Internal.Motherboard.Asrock;
using DriverUpdater.Services.Sources.Internal.Motherboard.Asus;
using DriverUpdater.Services.Sources.Internal.Motherboard.Gigabyte;
using DriverUpdater.Services.Sources.Internal.Motherboard.Msi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DriverUpdater.Services;

public static class ServicesServiceCollectionExtensions
{
    public static IServiceCollection AddDriverUpdaterServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IDriverScanService, DriverScanService>();
        services.AddSingleton<IUpdateSource, WindowsUpdateSource>();
        services.AddSingleton<IUpdateSource, MicrosoftCatalogSource>();
        services.AddSingleton<IUpdateSource, OemToolUpdateSource>();
        services.AddSingleton<IUpdateSource, OemSupportSource>();

        ConfigureVendorScrapingHttpClient(services, AmdGraphicsSource.HttpClientName, "https://www.amd.com/");
        ConfigureVendorScrapingHttpClient(services, AmdChipsetSource.HttpClientName, "https://www.amd.com/");
        ConfigureVendorScrapingHttpClient(services, NvidiaGraphicsSource.HttpClientName, "https://gfwsl.geforce.com/");
        ConfigureVendorScrapingHttpClient(services, OfficialVendorPageSource.HttpClientName, "https://example.com/");

        services.AddSingleton<IUpdateSource>(sp => new AmdGraphicsSource(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(AmdGraphicsSource.HttpClientName),
            sp.GetRequiredService<ILogger<AmdGraphicsSource>>()));
        services.AddSingleton<IAmdSocketDetector, AmdSocketDetector>();
        services.AddSingleton<IUpdateSource>(sp => new AmdChipsetSource(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(AmdChipsetSource.HttpClientName),
            sp.GetRequiredService<IAmdSocketDetector>(),
            sp.GetRequiredService<ILogger<AmdChipsetSource>>()));
        services.AddSingleton<IUpdateSource>(sp => new NvidiaGraphicsSource(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(NvidiaGraphicsSource.HttpClientName),
            sp.GetRequiredService<ILogger<NvidiaGraphicsSource>>()));

        ConfigureVendorInstallerDownloadHttpClient(services);
        ConfigureGigabyteHttpClient(services);
        services.AddSingleton(sp => new GigabyteApiScraper(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(GigabyteApiScraper.HttpClientName),
            sp.GetRequiredService<ILogger<GigabyteApiScraper>>()));
        services.AddSingleton<GigabytePlaywrightScraper>();
        services.AddSingleton<HybridGigabyteScraper>(sp => new HybridGigabyteScraper(
            sp.GetRequiredService<GigabyteApiScraper>(),
            new Lazy<IMotherboardScraper>(() => sp.GetRequiredService<GigabytePlaywrightScraper>()),
            sp.GetRequiredService<IOptionsMonitor<ScraperSettings>>(),
            sp.GetRequiredService<ILogger<HybridGigabyteScraper>>()));
        services.AddSingleton<AsusMotherboardScraper>();
        services.AddSingleton<MsiMotherboardScraper>();
        services.AddSingleton<AsrockMotherboardScraper>();
        services.AddSingleton<IUpdateSource>(sp => new MotherboardSource(
            sp.GetRequiredService<IOemDetectionService>(),
            new Dictionary<OemVendor, IMotherboardScraper>
            {
                [OemVendor.Gigabyte] = sp.GetRequiredService<HybridGigabyteScraper>(),
                [OemVendor.Asus] = sp.GetRequiredService<AsusMotherboardScraper>(),
                [OemVendor.Msi] = sp.GetRequiredService<MsiMotherboardScraper>(),
                [OemVendor.ASRock] = sp.GetRequiredService<AsrockMotherboardScraper>(),
            },
            sp.GetRequiredService<ILogger<MotherboardSource>>()));

        services.AddSingleton<IUpdateSource>(sp => new OfficialVendorPageSource(
            sp.GetRequiredService<ILogger<OfficialVendorPageSource>>(),
            httpClient: sp.GetRequiredService<IHttpClientFactory>().CreateClient(OfficialVendorPageSource.HttpClientName)));

        services.AddSingleton<IOemDetectionService, OemDetectionService>();
        services.AddSingleton<IBackupService, BackupService>();
        services.AddSingleton<IRestorePointService, RestorePointService>();
        ConfigureVendorPageResolverHttpClient(services);
        services.AddSingleton<IVendorPageInstallerResolver, VendorPageInstallerResolver>();
        services.AddSingleton<IInstallPipeline, InstallPipeline>();
        services.AddSingleton<IScheduledScanRunner, ScheduledScanRunner>();

        ConfigureAiHttpClient(services);
        services.AddSingleton<GeminiAiVerifier>();
        services.AddSingleton<OllamaAiVerifier>();
        services.AddSingleton<IAiVerifier, AiVerifierSelector>();

        return services;
    }

    private static void ConfigureAiHttpClient(IServiceCollection services)
    {
        services.AddHttpClient(GeminiAiVerifier.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(90);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("DriverUpdater/0.1 (+local)");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        });
    }

    private static void ConfigureVendorScrapingHttpClient(IServiceCollection services, string name, string baseAddress)
    {
        services.AddHttpClient(name, client =>
        {
            client.BaseAddress = new Uri(baseAddress);
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("DriverUpdater/0.1 (+local)");
            client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml");
        });
    }

    private static void ConfigureVendorInstallerDownloadHttpClient(IServiceCollection services)
    {
        // AMD's CDN 302-redirects non-browser User-Agents to an HTML "download-incomplete"
        // page, leaving us with HTML masquerading as .exe and Process.Start failing with
        // "file or directory is corrupted and unreadable". Mimic a Chrome request so the
        // CDN serves the actual PE binary.
        services.AddHttpClient(InstallPipeline.DownloadsHttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromMinutes(15);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            client.DefaultRequestHeaders.Accept.ParseAdd("*/*");
            client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
            client.DefaultRequestHeaders.Referrer = new Uri("https://www.google.com/");
        });
    }

    private static void ConfigureVendorPageResolverHttpClient(IServiceCollection services)
    {
        // Vendor support pages (AMD, Gigabyte, ...) 403 or redirect non-browser
        // User-Agents, so the resolver mimics Chrome like the downloads client does.
        services.AddHttpClient(VendorPageInstallerResolver.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml");
            client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        });
    }

    private static void ConfigureGigabyteHttpClient(IServiceCollection services)
    {
        // Gigabyte's edge is fronted by Akamai which 403s any non-browser UA. Mimic the
        // headers a Chrome request would carry so the lightweight API path stands a
        // chance before falling back to Playwright.
        services.AddHttpClient(GigabyteApiScraper.HttpClientName, client =>
        {
            client.BaseAddress = new Uri("https://www.gigabyte.com/");
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json,text/plain,*/*");
            client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        });
    }
}
