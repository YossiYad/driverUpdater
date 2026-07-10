using DriverUpdater.Core.Models;

namespace DriverUpdater.Core.Abstractions;

/// <summary>
/// Reads back the driver currently bound to a single device (by DeviceID). Used after an
/// install to verify that the active driver actually changed, rather than trusting the
/// installer's exit code - pnputil can report success after merely adding a package to the
/// driver store while Windows keeps a higher-ranked (e.g. inbox) driver bound to the device.
/// </summary>
public interface IInstalledDriverProbe
{
    /// <summary>
    /// Returns the current driver version/date for the given DeviceID, or null when the
    /// device cannot be found or the probe fails.
    /// </summary>
    Task<InstalledDriverState?> GetCurrentAsync(string deviceId, CancellationToken cancellationToken = default);
}
