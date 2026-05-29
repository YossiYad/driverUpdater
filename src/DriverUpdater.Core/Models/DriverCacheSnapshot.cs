namespace DriverUpdater.Core.Models;

public sealed record CachedDriverEntry(
    DriverInfo Driver,
    DriverStatus Status,
    UpdateCandidate? AvailableUpdate);

public sealed record DriverCacheSnapshot(
    DateTimeOffset CapturedAt,
    IReadOnlyList<CachedDriverEntry> Entries);
