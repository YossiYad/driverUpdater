namespace DriverUpdater.Core.Models;

/// <summary>
/// Records that an update was installed for a device but Windows immediately kept the existing
/// driver (post-install verification saw no version change, with no reboot pending). Such an
/// install is a proven no-op - usually a generic/mismatched catalog driver that Windows ranks
/// below the specialized installed one. Used to stop re-offering the same proven no-op every scan.
/// Reboot-required installs are deliberately NOT recorded here: they bind after a restart.
/// </summary>
/// <param name="DeviceId">The device the attempt targeted.</param>
/// <param name="TargetVersion">The candidate version that was attempted (string form).</param>
/// <param name="InstalledVersionAtAttempt">
/// The driver version installed when the attempt ran. The candidate is only suppressed on a later
/// scan while the device still reports this version - if it changes, the record no longer applies.
/// </param>
/// <param name="AttemptCount">How many times this exact target was proven ineffective.</param>
/// <param name="LastAttemptUtc">When the most recent proven-ineffective attempt ran.</param>
public sealed record IneffectiveUpdateRecord(
    string DeviceId,
    string TargetVersion,
    string? InstalledVersionAtAttempt,
    int AttemptCount,
    DateTimeOffset LastAttemptUtc);
