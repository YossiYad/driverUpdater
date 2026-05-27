namespace DriverUpdater.Core.Models;

public sealed record RestorePointInfo(
    string SequenceNumber,
    string Description,
    DateTimeOffset CreatedAt);
