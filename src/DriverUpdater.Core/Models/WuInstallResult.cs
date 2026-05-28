namespace DriverUpdater.Core.Models;

public sealed record WuInstallResult(
    int HResult,
    bool RebootRequired,
    string Message);
