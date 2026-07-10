using DriverUpdater.Core.Models;

namespace DriverUpdater.Core.Abstractions;

/// <summary>
/// Persists updates that were proven to have no effect (installed, but Windows kept the existing
/// driver with no reboot pending) so the app stops re-offering the same no-op on every scan.
/// </summary>
public interface IIneffectiveUpdateStore
{
    /// <summary>Loads all recorded proven-ineffective updates.</summary>
    Task<IReadOnlyList<IneffectiveUpdateRecord>> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Records (or increments) a proven-ineffective attempt for a device/target version, storing
    /// the driver version that was installed at the time so a later scan can tell whether anything
    /// changed since.
    /// </summary>
    Task RecordAsync(string deviceId, string targetVersion, string? installedVersion, CancellationToken cancellationToken = default);
}
