namespace DriverUpdater.Core.Models;

/// <summary>
/// One driver version observed on a device during a scan. Only metadata is stored - the driver
/// package itself stays in the Windows DriverStore, so keeping the whole version history costs
/// a few hundred bytes per device instead of gigabytes of exported packages.
/// </summary>
public sealed record DriverVersionRecord(
    string DeviceId,
    string DeviceName,
    string Version,
    DateOnly? DriverDate,
    string? InfName,
    string? Provider,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt);
