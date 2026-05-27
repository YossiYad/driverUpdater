namespace DriverUpdater.Core.Models;

public enum UpdateStatus
{
    Pending = 0,
    CreatingRestorePoint,
    BackingUp,
    Downloading,
    Installing,
    Succeeded,
    Failed,
    RolledBack,
    Skipped,
    Cancelled
}
