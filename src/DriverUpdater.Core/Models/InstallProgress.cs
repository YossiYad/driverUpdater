namespace DriverUpdater.Core.Models;

public sealed record InstallProgress(
    UpdateStatus Stage,
    int PercentComplete,
    string Message);
