using DriverUpdater.Core.Abstractions;
using DriverUpdater.Services.Backup;
using DriverUpdater.Services.Scanning;
using DriverUpdater.Services.Sources;
using Microsoft.Extensions.DependencyInjection;

namespace DriverUpdater.Services;

public static class ServicesServiceCollectionExtensions
{
    public static IServiceCollection AddDriverUpdaterServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IDriverScanService, DriverScanService>();
        services.AddSingleton<IUpdateSource, WindowsUpdateSource>();
        services.AddSingleton<IUpdateSource, MicrosoftCatalogSource>();
        services.AddSingleton<IOemDetectionService, OemDetectionService>();
        services.AddSingleton<IBackupService, BackupService>();
        services.AddSingleton<IRestorePointService, RestorePointService>();
        return services;
    }
}
