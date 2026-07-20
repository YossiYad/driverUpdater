namespace DriverUpdater.Core.Models;

public sealed record UpdateVerificationReport(
    Guid BatchId,
    DateTimeOffset CreatedAt,
    bool IsAfterRestart,
    IReadOnlyList<UpdateVerificationItem> Items,
    string? AiSummary,
    bool AiWasUsed)
{
    public int VerifiedCount => Items.Count(i => i.Status == UpdateVerificationStatus.VerifiedUpdated);

    public int PendingRestartCount => Items.Count(i => i.Status == UpdateVerificationStatus.PendingRestart);

    public int ManualActionCount => Items.Count(i => i.Status == UpdateVerificationStatus.ManualActionRequired);

    public int AttentionCount => Items.Count(i => i.Status is UpdateVerificationStatus.NotUpdated
        or UpdateVerificationStatus.Failed
        or UpdateVerificationStatus.Inconclusive);
}
