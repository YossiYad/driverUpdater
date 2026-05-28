namespace DriverUpdater.Core.Models;

public sealed record InstallOptions(
    bool CreateRestorePoint = true,
    bool BackupCurrentDriver = true,
    bool DryRun = false);
