using DriverUpdater.App.Logging;
using DriverUpdater.App.Services;
using DriverUpdater.App.ViewModels;
using DriverUpdater.App.Views;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Infrastructure;
using DriverUpdater.Services;
using Microsoft.Extensions.DependencyInjection;

namespace DriverUpdater.App;

public static class AppServices
{
    public static IServiceCollection AddDriverUpdaterApp(this IServiceCollection services, InMemoryLogSink logSink)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(logSink);

        services.AddDriverUpdaterInfrastructure();
        services.AddDriverUpdaterServices();
        services.AddSingleton<IInstallConfirmation, DialogInstallConfirmation>();
        services.AddSingleton<IAiScanConfirmation, DialogAiScanConfirmation>();
        services.AddSingleton<ILocalizationService, LocalizationService>();
        services.AddSingleton<IExternalLinkOpener, ExternalLinkOpener>();
        services.AddSingleton<ISupportWindowOpener, SupportWindowOpener>();
        services.AddSingleton<ILogCleanupService, LogCleanupService>();
        services.AddSingleton<ApplicationBehaviorState>();
        services.AddSingleton<NotificationAreaService>();
        services.AddHostedService<LogCleanupBackgroundService>();
        services.AddSingleton<IUpdatePageOpener, UpdatePageOpener>();
        services.AddSingleton<IAiResultTranslator, AiResultTranslator>();
        services.AddSingleton<IAiResultWindowOpener, AiResultWindowOpener>();
        services.AddSingleton<AiQuotaNotificationService>();
        services.AddSingleton<IAppUpdater, VelopackAppUpdater>();
        services.AddSingleton<IAppUpdatePrompt, DialogAppUpdatePrompt>();
        services.AddSingleton<IRebootPrompt, DialogRebootPrompt>();
        services.AddSingleton<IUpdateSummaryWindowOpener, UpdateSummaryWindowOpener>();
        services.AddSingleton<IPostUpdateSummaryCoordinator, PostUpdateSummaryCoordinator>();
        services.AddSingleton<IHistoryWindowOpener, HistoryWindowOpener>();
        services.AddSingleton<ISettingsWindowOpener, SettingsWindowOpener>();
        services.AddSingleton<ILogsWindowOpener, LogsWindowOpener>();
        services.AddSingleton(logSink);
        services.AddSingleton<MainViewModel>();
        services.AddTransient<HistoryViewModel>();
        services.AddTransient<HistoryWindow>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<SettingsWindow>();
        services.AddTransient<LogsViewModel>();
        services.AddTransient<LogsWindow>();
        services.AddSingleton<MainWindow>();
        return services;
    }
}
