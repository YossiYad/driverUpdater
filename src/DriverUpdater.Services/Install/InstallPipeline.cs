using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using Microsoft.Extensions.Logging;

namespace DriverUpdater.Services.Install;

public sealed class InstallPipeline : IInstallPipeline
{
    private readonly IRestorePointService _restorePointService;
    private readonly IBackupService _backupService;
    private readonly IWuApiClient _wuApiClient;
    private readonly IHistoryRepository? _historyRepository;
    private readonly ILogger<InstallPipeline> _logger;
    private readonly TimeProvider _clock;

    public InstallPipeline(
        IRestorePointService restorePointService,
        IBackupService backupService,
        IWuApiClient wuApiClient,
        ILogger<InstallPipeline> logger,
        IHistoryRepository? historyRepository = null,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(restorePointService);
        ArgumentNullException.ThrowIfNull(backupService);
        ArgumentNullException.ThrowIfNull(wuApiClient);
        ArgumentNullException.ThrowIfNull(logger);
        _restorePointService = restorePointService;
        _backupService = backupService;
        _wuApiClient = wuApiClient;
        _historyRepository = historyRepository;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<UpdateOperation> ExecuteAsync(
        UpdateOperation operation,
        InstallOptions options,
        IProgress<UpdateOperation>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(options);

        var recordingProgress = WrapWithRecorder(progress, cancellationToken);

        try
        {
            if (options.DryRun)
            {
                _logger.LogInformation("Dry run for {Device}", operation.TargetSnapshot.DeviceName);
                operation = operation with
                {
                    Status = UpdateStatus.Skipped,
                    ErrorMessage = BuildDryRunSummary(operation, options),
                    CompletedAt = _clock.GetUtcNow()
                };
                recordingProgress.Report(operation);
                return operation;
            }

            if (options.CreateRestorePoint)
            {
                operation = await StepCreateRestorePointAsync(operation, recordingProgress, cancellationToken).ConfigureAwait(false);
                if (operation.Status == UpdateStatus.Failed)
                {
                    return operation;
                }
            }

            if (options.BackupCurrentDriver)
            {
                operation = await StepBackupAsync(operation, recordingProgress, cancellationToken).ConfigureAwait(false);
                if (operation.Status == UpdateStatus.Failed)
                {
                    return operation;
                }
            }

            operation = await StepDownloadAndInstallAsync(operation, recordingProgress, cancellationToken).ConfigureAwait(false);
            return operation;
        }
        catch (OperationCanceledException)
        {
            operation = operation with
            {
                Status = UpdateStatus.Cancelled,
                ErrorMessage = "Operation cancelled by user.",
                CompletedAt = _clock.GetUtcNow()
            };
            recordingProgress.Report(operation);
            return operation;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected install pipeline failure");
            operation = operation with
            {
                Status = UpdateStatus.Failed,
                ErrorMessage = $"Unexpected error: {ex.Message}",
                CompletedAt = _clock.GetUtcNow()
            };
            recordingProgress.Report(operation);
            return operation;
        }
    }

    private IProgress<UpdateOperation> WrapWithRecorder(IProgress<UpdateOperation>? outer, CancellationToken cancellationToken) =>
        new RecordingProgress(outer, _historyRepository, _logger, cancellationToken);

    private sealed class RecordingProgress : IProgress<UpdateOperation>
    {
        private readonly IProgress<UpdateOperation>? _outer;
        private readonly IHistoryRepository? _repository;
        private readonly ILogger _logger;
        private readonly CancellationToken _cancellationToken;

        public RecordingProgress(IProgress<UpdateOperation>? outer, IHistoryRepository? repository, ILogger logger, CancellationToken cancellationToken)
        {
            _outer = outer;
            _repository = repository;
            _logger = logger;
            _cancellationToken = cancellationToken;
        }

        public void Report(UpdateOperation value)
        {
            _outer?.Report(value);
            if (_repository is not null)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _repository.UpsertOperationAsync(value, _cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to record operation {Id}", value.OperationId);
                    }
                }, CancellationToken.None);
            }
        }
    }

    private async Task<UpdateOperation> StepCreateRestorePointAsync(
        UpdateOperation operation,
        IProgress<UpdateOperation>? progress,
        CancellationToken cancellationToken)
    {
        operation = operation with { Status = UpdateStatus.CreatingRestorePoint };
        progress?.Report(operation);

        var description = $"DriverUpdater - before {operation.TargetSnapshot.DeviceName}";
        var rp = await _restorePointService.CreateRestorePointAsync(description, cancellationToken).ConfigureAwait(false);
        if (rp.IsFailure)
        {
            _logger.LogWarning("Restore point step failed: {Error}", rp.Error);
            operation = operation with
            {
                Status = UpdateStatus.Failed,
                ErrorMessage = $"Restore point: {rp.Error.Message}",
                CompletedAt = _clock.GetUtcNow()
            };
        }
        else
        {
            operation = operation with { RestorePointSequenceNumber = rp.Value.SequenceNumber };
        }
        progress?.Report(operation);
        return operation;
    }

    private async Task<UpdateOperation> StepBackupAsync(
        UpdateOperation operation,
        IProgress<UpdateOperation>? progress,
        CancellationToken cancellationToken)
    {
        operation = operation with { Status = UpdateStatus.BackingUp };
        progress?.Report(operation);

        var backup = await _backupService.BackupDriverAsync(operation.TargetSnapshot, cancellationToken).ConfigureAwait(false);
        if (backup.IsFailure)
        {
            _logger.LogWarning("Backup step failed: {Error}", backup.Error);
            operation = operation with
            {
                Status = UpdateStatus.Failed,
                ErrorMessage = $"Backup: {backup.Error.Message}",
                CompletedAt = _clock.GetUtcNow()
            };
        }
        else
        {
            operation = operation with { BackupPath = backup.Value.BackupFolderPath };
        }
        progress?.Report(operation);
        return operation;
    }

    private async Task<UpdateOperation> StepDownloadAndInstallAsync(
        UpdateOperation operation,
        IProgress<UpdateOperation>? progress,
        CancellationToken cancellationToken)
    {
        if (operation.Candidate.Source != UpdateSource.WindowsUpdate)
        {
            operation = operation with
            {
                Status = UpdateStatus.Skipped,
                ErrorMessage = $"{operation.Candidate.Source} installs are not yet supported by the pipeline.",
                CompletedAt = _clock.GetUtcNow()
            };
            progress?.Report(operation);
            return operation;
        }

        operation = operation with { Status = UpdateStatus.Downloading };
        progress?.Report(operation);

        operation = operation with { Status = UpdateStatus.Installing };
        progress?.Report(operation);

        var install = await _wuApiClient.DownloadAndInstallAsync(operation.Candidate.SourceUpdateId, cancellationToken).ConfigureAwait(false);
        if (install.IsFailure)
        {
            _logger.LogError("Install failed: {Error}", install.Error);
            operation = operation with
            {
                Status = UpdateStatus.Failed,
                ErrorMessage = install.Error.Message,
                CompletedAt = _clock.GetUtcNow()
            };
            progress?.Report(operation);
            return operation;
        }

        operation = operation with
        {
            Status = UpdateStatus.Succeeded,
            ErrorMessage = install.Value.RebootRequired ? "Reboot required to complete installation." : null,
            CompletedAt = _clock.GetUtcNow()
        };
        progress?.Report(operation);
        return operation;
    }

    internal static string BuildDryRunSummary(UpdateOperation operation, InstallOptions options)
    {
        var lines = new List<string>();
        if (options.CreateRestorePoint)
        {
            lines.Add($"1. Create system restore point: \"DriverUpdater - before {operation.TargetSnapshot.DeviceName}\"");
        }
        if (options.BackupCurrentDriver)
        {
            lines.Add($"{lines.Count + 1}. Back up current driver ({operation.TargetSnapshot.InfName ?? "INF unknown"}) to %ProgramData%\\DriverUpdater\\Backups");
        }
        lines.Add($"{lines.Count + 1}. Download from {operation.Candidate.Source} ({operation.Candidate.DownloadUrl})");
        lines.Add($"{lines.Count + 1}. Install version {operation.Candidate.NewVersion}, {operation.Candidate.SizeBytes:N0} bytes");
        return string.Join('\n', lines);
    }
}
