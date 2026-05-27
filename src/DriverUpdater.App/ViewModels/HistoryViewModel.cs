using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using Microsoft.Extensions.Logging;

namespace DriverUpdater.App.ViewModels;

public partial class HistoryViewModel : ObservableObject
{
    private readonly IHistoryRepository _historyRepository;
    private readonly IBackupService _backupService;
    private readonly ILogger<HistoryViewModel> _logger;

    public ObservableCollection<HistoryRowViewModel> Operations { get; } = new();

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusText = "Loading history...";

    public HistoryViewModel(
        IHistoryRepository historyRepository,
        IBackupService backupService,
        ILogger<HistoryViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(historyRepository);
        ArgumentNullException.ThrowIfNull(backupService);
        ArgumentNullException.ThrowIfNull(logger);
        _historyRepository = historyRepository;
        _backupService = backupService;
        _logger = logger;
    }

    [RelayCommand]
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        IsLoading = true;
        StatusText = "Loading history...";
        try
        {
            await _historyRepository.InitializeAsync(cancellationToken).ConfigureAwait(true);
            var operations = await _historyRepository.ListOperationsAsync(200, cancellationToken).ConfigureAwait(true);
            Operations.Clear();
            foreach (var op in operations)
            {
                Operations.Add(new HistoryRowViewModel(op));
            }
            StatusText = Operations.Count == 0
                ? "No history yet. Run an update and the operation will appear here."
                : $"{Operations.Count} operations.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not load history");
            StatusText = $"Could not load history: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    public async Task RollbackAsync(HistoryRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!row.CanRollback || string.IsNullOrEmpty(row.Operation.BackupPath))
        {
            return;
        }

        row.IsRollingBack = true;
        row.RollbackError = null;
        try
        {
            var artifact = new BackupArtifact(
                DriverInfName: row.Operation.TargetSnapshot.InfName ?? string.Empty,
                DriverDeviceName: row.Operation.TargetSnapshot.DeviceName,
                BackupFolderPath: row.Operation.BackupPath!,
                CreatedAt: row.Operation.StartedAt,
                SizeBytes: 0);

            var result = await _backupService.RestoreFromBackupAsync(artifact).ConfigureAwait(true);
            if (result.IsFailure)
            {
                row.RollbackError = result.Error.Message;
                StatusText = $"Rollback failed: {result.Error.Message}";
                return;
            }

            var updated = row.Operation with { Status = UpdateStatus.RolledBack };
            await _historyRepository.UpsertOperationAsync(updated).ConfigureAwait(true);
            var newIndex = Operations.IndexOf(row);
            if (newIndex >= 0)
            {
                Operations[newIndex] = new HistoryRowViewModel(updated);
            }
            StatusText = $"Rolled back {row.DeviceName}.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Rollback failed");
            row.RollbackError = ex.Message;
            StatusText = $"Rollback failed: {ex.Message}";
        }
        finally
        {
            row.IsRollingBack = false;
        }
    }
}
