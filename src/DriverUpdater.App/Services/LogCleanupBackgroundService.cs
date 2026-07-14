using DriverUpdater.Core.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DriverUpdater.App.Services;

public sealed class LogCleanupBackgroundService : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromDays(1);

    private readonly ISettingsStore _settingsStore;
    private readonly ILogCleanupService _cleanupService;
    private readonly ILogger<LogCleanupBackgroundService> _logger;

    public LogCleanupBackgroundService(
        ISettingsStore settingsStore,
        ILogCleanupService cleanupService,
        ILogger<LogCleanupBackgroundService> logger)
    {
        ArgumentNullException.ThrowIfNull(settingsStore);
        ArgumentNullException.ThrowIfNull(cleanupService);
        ArgumentNullException.ThrowIfNull(logger);
        _settingsStore = settingsStore;
        _cleanupService = cleanupService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var settings = await _settingsStore.LoadAsync(stoppingToken).ConfigureAwait(false);
                await _cleanupService.CleanupAsync(settings.LogCleanup, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Automatic log cleanup failed");
            }

            try
            {
                await Task.Delay(CheckInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
