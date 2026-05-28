using CommunityToolkit.Mvvm.ComponentModel;
using DriverUpdater.Core.Models;

namespace DriverUpdater.App.ViewModels;

public partial class HistoryRowViewModel : ObservableObject
{
    public UpdateOperation Operation { get; }

    [ObservableProperty]
    private bool _isRollingBack;

    [ObservableProperty]
    private string? _rollbackError;

    public HistoryRowViewModel(UpdateOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        Operation = operation;
    }

    public Guid OperationId => Operation.OperationId;
    public string DeviceName => Operation.TargetSnapshot.DeviceName;
    public string Source => Operation.Candidate.Source.ToString();
    public string Status => Operation.Status.ToString();
    public string StartedAtText => Operation.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public string? CompletedAtText => Operation.CompletedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public string FromVersionText => Operation.TargetSnapshot.CurrentVersion?.ToString() ?? "n/a";
    public string ToVersionText => Operation.Candidate.NewVersion.ToString();
    public bool CanRollback =>
        Operation.Status == UpdateStatus.Succeeded
        && !string.IsNullOrEmpty(Operation.BackupPath);
}
