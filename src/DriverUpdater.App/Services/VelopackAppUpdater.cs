using DriverUpdater.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Velopack;
using Velopack.Sources;

namespace DriverUpdater.App.Services;

public sealed class VelopackAppUpdater : IAppUpdater
{
    private readonly IOptionsMonitor<UpdaterSettings> _settings;
    private readonly ILogger<VelopackAppUpdater> _logger;

    public VelopackAppUpdater(IOptionsMonitor<UpdaterSettings> settings, ILogger<VelopackAppUpdater> logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);
        _settings = settings;
        _logger = logger;
    }

    public async Task CheckAndApplyAsync(CancellationToken cancellationToken = default)
    {
        var settings = _settings.CurrentValue;
        if (!settings.CheckOnStartup || string.IsNullOrWhiteSpace(settings.FeedUrl))
        {
            return;
        }

        try
        {
            var source = new SimpleWebSource(settings.FeedUrl);
            var manager = new UpdateManager(source);
            if (!manager.IsInstalled)
            {
                _logger.LogDebug("Skipping update check: app is not installed via Velopack");
                return;
            }

            var info = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (info is null)
            {
                _logger.LogInformation("No app updates available");
                return;
            }

            _logger.LogInformation("App update {Version} available", info.TargetFullRelease.Version);
            await manager.DownloadUpdatesAsync(info, cancelToken: cancellationToken).ConfigureAwait(false);

            if (settings.AutoApply)
            {
                _logger.LogInformation("Auto-apply enabled, restarting to apply {Version}", info.TargetFullRelease.Version);
                manager.ApplyUpdatesAndRestart(info);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "App self-update check failed");
        }
    }
}
