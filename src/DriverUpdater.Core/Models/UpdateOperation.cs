namespace DriverUpdater.Core.Models;

public sealed record UpdateOperation(
    Guid OperationId,
    UpdateCandidate Candidate,
    DriverInfo TargetSnapshot,
    UpdateStatus Status,
    string? ErrorMessage,
    string? BackupPath,
    string? RestorePointSequenceNumber,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt)
{
    public bool IsTerminal => Status is UpdateStatus.Succeeded
        or UpdateStatus.Failed
        or UpdateStatus.RolledBack
        or UpdateStatus.Skipped
        or UpdateStatus.Cancelled;

    public TimeSpan? Duration => CompletedAt is { } completed
        ? completed - StartedAt
        : null;

    public static UpdateOperation NewPending(UpdateCandidate candidate, DriverInfo target) =>
        new(OperationId: Guid.NewGuid(),
            Candidate: candidate,
            TargetSnapshot: target,
            Status: UpdateStatus.Pending,
            ErrorMessage: null,
            BackupPath: null,
            RestorePointSequenceNumber: null,
            StartedAt: DateTimeOffset.UtcNow,
            CompletedAt: null);
}
