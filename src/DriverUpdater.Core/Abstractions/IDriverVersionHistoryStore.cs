using DriverUpdater.Core.Models;

namespace DriverUpdater.Core.Abstractions;

/// <summary>
/// Persists the driver versions seen on each device across scans, so an older version can later
/// be offered as a downgrade target. Records are metadata only; whether the matching package is
/// still installable is checked against the Windows DriverStore at downgrade time.
/// </summary>
public interface IDriverVersionHistoryStore
{
    /// <summary>Upserts one record per (device, version) from the given scan snapshot.</summary>
    Task RecordScanAsync(IReadOnlyList<DriverInfo> drivers, CancellationToken cancellationToken = default);

    /// <summary>All recorded versions for the device, newest LastSeenAt first.</summary>
    Task<IReadOnlyList<DriverVersionRecord>> GetHistoryAsync(string deviceId, CancellationToken cancellationToken = default);
}
