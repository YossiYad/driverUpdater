namespace DriverUpdater.Core.Models;

public sealed record PendingUpdateVerificationBatch(
    Guid BatchId,
    DateTimeOffset CreatedAt,
    IReadOnlyList<UpdateOperation> Operations);
