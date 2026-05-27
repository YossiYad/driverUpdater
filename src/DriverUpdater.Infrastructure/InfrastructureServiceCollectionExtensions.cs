using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Options;
using DriverUpdater.Infrastructure.Catalog;
using DriverUpdater.Infrastructure.History;
using DriverUpdater.Infrastructure.PnPUtil;
using DriverUpdater.Infrastructure.Powershell;
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
        services.AddSingleton<IHistoryRepository, SqliteHistoryRepository>();
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
