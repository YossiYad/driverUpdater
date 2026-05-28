using DriverUpdater.Core.Abstractions;
using DriverUpdater.Services.Backup;
using DriverUpdater.Services.Install;
using DriverUpdater.Services.Scanning;
using DriverUpdater.Services.Sources;
using DriverUpdater.Services.Sources.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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

        services.AddSingleton<IUpdateSource>(sp => new AmdGraphicsSource(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(AmdGraphicsSource.HttpClientName),
            sp.GetRequiredService<ILogger<AmdGraphicsSource>>()));
        services.AddSingleton<IAmdSocketDetector, AmdSocketDetector>();
        services.AddSingleton<IUpdateSource>(sp => new AmdChipsetSource(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(AmdChipsetSource.HttpClientName),
            sp.GetRequiredService<IAmdSocketDetector>(),
            sp.GetRequiredService<ILogger<AmdChipsetSource>>()));
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
}
