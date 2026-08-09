namespace DriverUpdater.Core.Models;

/// <summary>Result of a completed downgrade attempt, verified against the device's bound driver.</summary>
public sealed record DriverDowngradeOutcome(
    string DeviceId,
    string TargetVersion,
    string? BoundVersionAfter,
    bool VerifiedDowngraded,
    string? BackupFolderPath);
