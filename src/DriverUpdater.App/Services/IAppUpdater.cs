namespace DriverUpdater.App.Services;

public interface IAppUpdater
{
    /// <summary>
    /// Startup path: honours <c>CheckOnStartup</c>/<c>AutoApply</c>. When auto-apply is on
    /// and an update is found it downloads and restarts; otherwise it just records the
    /// pending update so a later <see cref="DownloadAndApplyAsync"/> can install it.
    /// </summary>
    Task CheckAndApplyAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks the configured feed (GitHub by default) for a newer release. The result
    /// distinguishes no update from a portable/non-Velopack install and from a failed check.
    /// </summary>
    Task<AppUpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads the pending update (re-checking first if needed) and restarts the app onto
    /// the new version. Does nothing when no update is available.
    /// </summary>
    Task DownloadAndApplyAsync(IProgress<int>? progress = null, CancellationToken cancellationToken = default);
}

public enum AppUpdateCheckStatus
{
    NoUpdate,
    UpdateAvailable,
    NotInstalled,
    NotConfigured,
    Failed
}

public sealed record AppUpdateCheckResult(AppUpdateCheckStatus Status, string? Version = null)
{
    public bool IsUpdateAvailable => Status == AppUpdateCheckStatus.UpdateAvailable;

    public static readonly AppUpdateCheckResult None = new(AppUpdateCheckStatus.NoUpdate);
    public static readonly AppUpdateCheckResult NotInstalled = new(AppUpdateCheckStatus.NotInstalled);
    public static readonly AppUpdateCheckResult NotConfigured = new(AppUpdateCheckStatus.NotConfigured);
    public static readonly AppUpdateCheckResult Failed = new(AppUpdateCheckStatus.Failed);

    public static AppUpdateCheckResult Available(string version) =>
        new(AppUpdateCheckStatus.UpdateAvailable, version);
}
