using DriverUpdater.Core.Abstractions;
using DriverUpdater.Infrastructure.Wmi;
using Microsoft.Extensions.DependencyInjection;

namespace DriverUpdater.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddDriverUpdaterInfrastructure(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IWmiQueryRunner, WmiQueryRunner>();
        return services;
    }
}
