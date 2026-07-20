using System.IO;
using DriverUpdater.Core.Options;
using Microsoft.Extensions.Logging;

namespace DriverUpdater.App.Services;

public sealed class LogCleanupService : ILogCleanupService
{
    public const string LogFilePattern = "driverupdater-*.log";

    private readonly ILogger<LogCleanupService> _logger;
    private readonly TimeProvider _timeProvider;

    public LogCleanupService(
        ILogger<LogCleanupService> logger,
        string? logDirectory = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        LogDirectory = logDirectory ?? DefaultLogDirectory();
    }

    public string LogDirectory { get; }

    public Task<int> CleanupAsync(
        LogCleanupSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!settings.Enabled || !Directory.Exists(LogDirectory))
        {
            return Task.FromResult(0);
        }

        var retentionDays = Math.Clamp(
            settings.RetentionDays,
            LogCleanupSettings.MinimumRetentionDays,
            LogCleanupSettings.MaximumRetentionDays);
        var cutoffUtc = _timeProvider.GetUtcNow().UtcDateTime.AddDays(-retentionDays);
        var deletedCount = 0;

        try
        {
            foreach (var path in Directory.EnumerateFiles(
                LogDirectory,
                LogFilePattern,
                SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    if (File.GetLastWriteTimeUtc(path) > cutoffUtc)
                    {
                        continue;
                    }

                    File.Delete(path);
                    deletedCount++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(ex, "Could not delete old log file {LogFile}", path);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not inspect the log directory {LogDirectory}", LogDirectory);
        }

        if (deletedCount > 0)
        {
            _logger.LogInformation(
                "Deleted {DeletedCount} log files older than {RetentionDays} days",
                deletedCount,
                retentionDays);
        }

        return Task.FromResult(deletedCount);
    }

    public static string DefaultLogDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "DriverUpdater",
        "Logs");
}
