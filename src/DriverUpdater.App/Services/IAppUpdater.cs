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
    /// Checks the configured feed (GitHub by default) for a newer release. Safe to call at
    /// any time; returns <see cref="AppUpdateCheckResult.None"/> when the app is not
    /// installed via Velopack, no feed is configured, or no update is available.
    /// </summary>
    Task<AppUpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads the pending update (re-checking first if needed) and restarts the app onto
    /// the new version. Does nothing when no update is available.
    /// </summary>
    Task DownloadAndApplyAsync(IProgress<int>? progress = null, CancellationToken cancellationToken = default);
}

public sealed record AppUpdateCheckResult(bool IsUpdateAvailable, string? Version)
{
    public static readonly AppUpdateCheckResult None = new(false, null);
}
