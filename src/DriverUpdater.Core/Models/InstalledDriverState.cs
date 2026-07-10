namespace DriverUpdater.Core.Models;

/// <summary>
/// The version/date of the driver currently bound to a device, as read back from the OS
/// after an install to confirm whether the active driver actually changed.
/// </summary>
public sealed record InstalledDriverState(Version? Version, DateOnly? Date);
