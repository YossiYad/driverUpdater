namespace DriverUpdater.Core.Models;

public enum UpdateVerificationStatus
{
    VerifiedUpdated = 0,
    PendingRestart,
    NotUpdated,
    Failed,
    Skipped,
    Inconclusive
}
