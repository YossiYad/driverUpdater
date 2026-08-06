using DriverUpdater.Core.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DriverUpdater.App.Services;

public sealed class LogCleanupBackgroundService : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromDays(1);

    private readonly ISettingsStore _settingsStore;
    private readonly ILogCleanupService _cleanupService;
    private readonly IBackupService? _backupService;
    private readonly ILogger<LogCleanupBackgroundService> _logger;

    public LogCleanupBackgroundService(
        ISettingsStore settingsStore,
        ILogCleanupService cleanupService,
        ILogger<LogCleanupBackgroundService> logger,
        IBackupService? backupService = null)
    {
        ArgumentNullException.ThrowIfNull(settingsStore);
        ArgumentNullException.ThrowIfNull(cleanupService);
        ArgumentNullException.ThrowIfNull(logger);
        _settingsStore = settingsStore;
        _cleanupService = cleanupService;
        _backupService = backupService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var settings = await _settingsStore.LoadAsync(stoppingToken).ConfigureAwait(false);
                try
                {
                    await _cleanupService.CleanupAsync(settings.LogCleanup, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Automatic log cleanup failed");
                }

                try
                {
                    var backupRetentionDays = Math.Max(1, settings.Backup.RetentionDays);
                    var purgedBackups = _backupService?.PurgeBackupsOlderThan(TimeSpan.FromDays(backupRetentionDays)) ?? 0;
                    if (purgedBackups > 0)
                    {
                        _logger.LogInformation(
                            "Removed {BackupCount} driver backup folder(s) older than {RetentionDays} days",
                            purgedBackups,
                            backupRetentionDays);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Automatic driver backup cleanup failed");
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Automatic housekeeping could not read the saved settings");
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
