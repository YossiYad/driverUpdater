using DriverUpdater.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Velopack;
using Velopack.Exceptions;
using Velopack.Sources;

namespace DriverUpdater.App.Services;

public sealed class VelopackAppUpdater : IAppUpdater
{
    private readonly IOptionsMonitor<UpdaterSettings> _settings;
    private readonly ILogger<VelopackAppUpdater> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private UpdateManager? _manager;
    private UpdateInfo? _pendingUpdate;

    public VelopackAppUpdater(IOptionsMonitor<UpdaterSettings> settings, ILogger<VelopackAppUpdater> logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);
        _settings = settings;
        _logger = logger;
    }

    public async Task CheckAndApplyAsync(CancellationToken cancellationToken = default)
    {
        if (!_settings.CurrentValue.CheckOnStartup)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await CheckForUpdatesCoreAsync().ConfigureAwait(false);
            if (result.IsUpdateAvailable && _settings.CurrentValue.AutoApply)
            {
                _logger.LogInformation("Auto-apply enabled, applying {Version}", result.Version);
                await DownloadAndApplyCoreAsync(progress: null, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "App self-update failed");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AppUpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await CheckForUpdatesCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DownloadAndApplyAsync(IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Re-check if we have no pending update cached (e.g. called directly).
            if (_manager is null || _pendingUpdate is null)
            {
                var result = await CheckForUpdatesCoreAsync().ConfigureAwait(false);
                if (!result.IsUpdateAvailable)
                {
                    _logger.LogInformation("No app update to download (status: {Status})", result.Status);
                    return;
                }
            }

            await DownloadAndApplyCoreAsync(progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<AppUpdateCheckResult> CheckForUpdatesCoreAsync()
    {
        try
        {
            var manager = CreateManager();
            if (manager is null)
            {
                ClearPendingUpdate();
                _logger.LogDebug("Skipping update check: no update feed is configured");
                return AppUpdateCheckResult.NotConfigured;
            }
            if (!manager.IsInstalled)
            {
                ClearPendingUpdate();
                _logger.LogDebug("Skipping update check: app is not installed via Velopack");
                return AppUpdateCheckResult.NotInstalled;
            }

            var info = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (info is null)
            {
                ClearPendingUpdate();
                _logger.LogInformation("No app updates available");
                return AppUpdateCheckResult.None;
            }

            _manager = manager;
            _pendingUpdate = info;
            var version = info.TargetFullRelease.Version.ToString();
            _logger.LogInformation("App update {Version} available", version);
            return AppUpdateCheckResult.Available(version);
        }
        catch (NotInstalledException ex)
        {
            ClearPendingUpdate();
            _logger.LogDebug(ex, "Skipping update check: app is not installed via Velopack");
            return AppUpdateCheckResult.NotInstalled;
        }
        catch (Exception ex)
        {
            ClearPendingUpdate();
            _logger.LogWarning(ex, "App self-update check failed");
            return AppUpdateCheckResult.Failed;
        }
    }

    private async Task DownloadAndApplyCoreAsync(IProgress<int>? progress, CancellationToken cancellationToken)
    {
        var manager = _manager;
        var info = _pendingUpdate;
        if (manager is null || info is null)
        {
            return;
        }

        _logger.LogInformation("Downloading app update {Version}", info.TargetFullRelease.Version);
        await manager.DownloadUpdatesAsync(
            info,
            progress is null ? null : progress.Report,
            cancelToken: cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Applying app update {Version} and restarting", info.TargetFullRelease.Version);
        manager.ApplyUpdatesAndRestart(info);
    }

    private void ClearPendingUpdate()
    {
        _manager = null;
        _pendingUpdate = null;
    }

    private UpdateManager? CreateManager()
    {
        var settings = _settings.CurrentValue;

        IUpdateSource? source = null;
        if (!string.IsNullOrWhiteSpace(settings.GitHubRepoUrl))
        {
            source = new GithubSource(settings.GitHubRepoUrl, accessToken: null, prerelease: settings.AllowPrerelease);
        }
        else if (!string.IsNullOrWhiteSpace(settings.FeedUrl))
        {
            source = new SimpleWebSource(settings.FeedUrl);
        }

        return source is null ? null : new UpdateManager(source);
    }
}
