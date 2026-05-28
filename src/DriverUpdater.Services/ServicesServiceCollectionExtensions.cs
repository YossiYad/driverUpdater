using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Options;
using DriverUpdater.Services.Backup;
using DriverUpdater.Services.Install;
using DriverUpdater.Services.Scanning;
using DriverUpdater.Services.Sources;
using DriverUpdater.Services.Sources.Internal;
using DriverUpdater.Services.Sources.Internal.Gigabyte;
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

        ConfigureGigabyteHttpClient(services);
        services.AddSingleton(sp => new GigabyteApiScraper(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(GigabyteApiScraper.HttpClientName),
            sp.GetRequiredService<ILogger<GigabyteApiScraper>>()));
        services.AddSingleton<GigabytePlaywrightScraper>();
        services.AddSingleton<IGigabyteScraper>(sp => new HybridGigabyteScraper(
            sp.GetRequiredService<GigabyteApiScraper>(),
            new Lazy<IGigabyteScraper>(() => sp.GetRequiredService<GigabytePlaywrightScraper>()),
            sp.GetRequiredService<IOptionsMonitor<ScraperSettings>>(),
            sp.GetRequiredService<ILogger<HybridGigabyteScraper>>()));
        services.AddSingleton<IUpdateSource, GigabyteMotherboardSource>();

        services.AddSingleton<IUpdateSource, OfficialVendorPageSource>();

        services.AddSingleton<IOemDetectionService, OemDetectionService>();
        services.AddSingleton<IBackupService, BackupService>();
        services.AddSingleton<IRestorePointService, RestorePointService>();
        services.AddSingleton<IInstallPipeline, InstallPipeline>();
        return services;
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
