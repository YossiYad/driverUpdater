using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Options;
using DriverUpdater.Infrastructure.Cache;
using DriverUpdater.Infrastructure.Catalog;
using DriverUpdater.Infrastructure.History;
using DriverUpdater.Infrastructure.PnPUtil;
using DriverUpdater.Infrastructure.Powershell;
using DriverUpdater.Infrastructure.Scheduling;
using DriverUpdater.Infrastructure.Settings;
using DriverUpdater.Infrastructure.Software;
using DriverUpdater.Infrastructure.VendorInstallers;
using DriverUpdater.Infrastructure.Wmi;
using DriverUpdater.Infrastructure.WuApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Polly;

namespace DriverUpdater.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddDriverUpdaterInfrastructure(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IWmiQueryRunner, WmiQueryRunner>();
        services.AddSingleton<IWuApiClient, WuApiClient>();
        services.AddSingleton<IPnPUtilRunner, PnPUtilRunner>();
        services.AddSingleton<IPowerShellInvoker, PowerShellInvoker>();
        services.AddSingleton<IVendorInstallerRunner, VendorInstallerRunner>();
        services.AddSingleton<IInstalledSoftwareVersionProvider, WindowsInstalledSoftwareVersionProvider>();
        services.AddSingleton<IHistoryRepository, SqliteHistoryRepository>();
        services.AddSingleton<ISchedulerService, WindowsTaskSchedulerService>();
        services.AddSingleton<ISettingsStore>(sp =>
            new JsonSettingsStore(sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<JsonSettingsStore>>()));
        services.AddSingleton<IDriverCacheStore>(sp =>
            new JsonDriverCacheStore(sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<JsonDriverCacheStore>>()));
        services.AddSingleton<IIneffectiveUpdateStore>(sp =>
            new JsonIneffectiveUpdateStore(sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<JsonIneffectiveUpdateStore>>()));
        services.AddSingleton<IPendingUpdateVerificationStore>(sp =>
            new JsonPendingUpdateVerificationStore(sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<JsonPendingUpdateVerificationStore>>()));
        services.AddSingleton<IPostRebootStartupService, WindowsPostRebootStartupService>();
        services.AddSingleton<ISystemBootTimeProvider, SystemBootTimeProvider>();
        services.AddMemoryCache();

        services.AddHttpClient<ICatalogHttpClient, CatalogHttpClient>(CatalogHttpClient.HttpClientName, (sp, client) =>
        {
            var settings = sp.GetRequiredService<IOptions<CatalogSettings>>().Value;
            client.BaseAddress = new Uri("https://www.catalog.update.microsoft.com/");
            client.Timeout = settings.RequestTimeout;
            client.DefaultRequestHeaders.UserAgent.ParseAdd("DriverUpdater/0.1 (+local)");
            client.DefaultRequestHeaders.Accept.ParseAdd("text/html");
        })
        .AddTransientHttpErrorPolicy(builder => builder.WaitAndRetryAsync(
            retryCount: 3,
            sleepDurationProvider: attempt => TimeSpan.FromMilliseconds(200 * Math.Pow(2, attempt))));

        return services;
    }
}
