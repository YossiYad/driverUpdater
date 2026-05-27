using DriverUpdater.Core.Abstractions;
using DriverUpdater.Services.Scanning;
using Microsoft.Extensions.DependencyInjection;

namespace DriverUpdater.Services;

public static class ServicesServiceCollectionExtensions
{
    public static IServiceCollection AddDriverUpdaterServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IDriverScanService, DriverScanService>();
        return services;
    }
}
