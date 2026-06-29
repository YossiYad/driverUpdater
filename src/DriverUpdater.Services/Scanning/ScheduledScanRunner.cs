using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using DriverUpdater.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DriverUpdater.Services.Scanning;

public sealed class ScheduledScanRunner : IScheduledScanRunner
{
    // Unattended installs always take the safe path: a system restore point and a per-driver
    // backup before each install, regardless of what the interactive confirmation dialog
    // would have offered. There is nobody watching to recover from a bad install.
    private static readonly InstallOptions UnattendedInstallOptions =
        new(CreateRestorePoint: true, BackupCurrentDriver: true, DryRun: false);

    private readonly IDriverScanService _scanService;
    private readonly IReadOnlyList<IUpdateSource> _updateSources;
    private readonly IInstallPipeline _installPipeline;
    private readonly IDriverCacheStore _driverCacheStore;
    private readonly IOptionsMonitor<UpdaterSettings> _updaterSettings;
    private readonly ILogger<ScheduledScanRunner> _logger;

    public ScheduledScanRunner(
        IDriverScanService scanService,
        IEnumerable<IUpdateSource> updateSources,
        IInstallPipeline installPipeline,
        IDriverCacheStore driverCacheStore,
        IOptionsMonitor<UpdaterSettings> updaterSettings,
        ILogger<ScheduledScanRunner> logger)
    {
        ArgumentNullException.ThrowIfNull(scanService);
        ArgumentNullException.ThrowIfNull(updateSources);
        ArgumentNullException.ThrowIfNull(installPipeline);
        ArgumentNullException.ThrowIfNull(driverCacheStore);
        ArgumentNullException.ThrowIfNull(updaterSettings);
        ArgumentNullException.ThrowIfNull(logger);
        _scanService = scanService;
        _updateSources = updateSources.ToArray();
        _installPipeline = installPipeline;
        _driverCacheStore = driverCacheStore;
        _updaterSettings = updaterSettings;
        _logger = logger;
    }

    public async Task RunAsync(bool installUpdates, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Scheduled run started (installUpdates={Install})", installUpdates);

        var states = new List<DriverState>();
        await foreach (var driver in _scanService.ScanAsync(cancellationToken).ConfigureAwait(false))
        {
            states.Add(new DriverState(driver));
        }
        _logger.LogInformation("Scheduled scan found {Count} drivers", states.Count);

        if (states.Count > 0)
        {
            await QueryUpdateSourcesAsync(states, cancellationToken).ConfigureAwait(false);
        }

        var outdated = states.Count(s => s.Candidate is not null);
        _logger.LogInformation("Scheduled scan matched {Count} update(s)", outdated);

        if (installUpdates && outdated > 0)
        {
            await InstallAsync(states, cancellationToken).ConfigureAwait(false);
        }

        await SaveCacheAsync(states, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Scheduled run completed");
    }

    private async Task QueryUpdateSourcesAsync(List<DriverState> states, CancellationToken cancellationToken)
    {
        // First state per hardware ID is the match target, mirroring the interactive grid
        // which binds a candidate to the first row in its hardware-ID bucket.
        var index = new Dictionary<string, DriverState>(StringComparer.OrdinalIgnoreCase);
        foreach (var state in states)
        {
            var key = state.Driver.HardwareId;
            if (!string.IsNullOrWhiteSpace(key) && !index.ContainsKey(key))
            {
                index[key] = state;
            }
        }

        var snapshots = states.Select(s => s.Driver).ToArray();
        var settings = _updaterSettings.CurrentValue;

        foreach (var source in _updateSources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsSourceDisabled(source, settings))
            {
                _logger.LogInformation("Scheduled scan skipping {Source}: disabled in settings", source.DisplayName);
                continue;
            }

            try
            {
                _logger.LogInformation("Scheduled scan querying {Source}", source.DisplayName);
                await foreach (var candidate in source.SearchAsync(snapshots, cancellationToken).ConfigureAwait(false))
                {
                    if (TryFind(index, candidate.ForHardwareId, out var state)
                        && candidate.IsNewerThan(state.Driver)
                        && DriverUpdateMatcher.ShouldReplace(state.Candidate, candidate))
                    {
                        state.Candidate = candidate;
                        state.Status = DriverStatus.Outdated;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scheduled scan source {Source} failed", source.DisplayName);
            }
        }
    }

    private async Task InstallAsync(List<DriverState> states, CancellationToken cancellationToken)
    {
        var processed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var state in states)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = state.Candidate;
            if (candidate is null || !IsInstallable(candidate.InstallKind))
            {
                continue;
            }
            // Many device rows can share one installer (e.g. an AMD chipset package). Run
            // each installer once and fan the outcome out to every row that shares its id.
            if (!processed.Add(candidate.SourceUpdateId))
            {
                continue;
            }

            _logger.LogInformation(
                "Scheduled install starting for {Device} (kind={Kind}, url={Url})",
                state.Driver.DeviceName, candidate.InstallKind, candidate.DownloadUrl);

            UpdateOperation finished;
            try
            {
                var op = UpdateOperation.NewPending(candidate, state.Driver);
                finished = await _installPipeline
                    .ExecuteAsync(op, UnattendedInstallOptions, progress: null, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scheduled install threw for {Device}", state.Driver.DeviceName);
                continue;
            }

            ApplyOutcome(states, candidate.SourceUpdateId, finished.Status);
            _logger.LogInformation(
                "Scheduled install for {Device} finished with {Status}{Error}",
                state.Driver.DeviceName, finished.Status,
                string.IsNullOrWhiteSpace(finished.ErrorMessage) ? string.Empty : " - " + finished.ErrorMessage);
        }
    }

    private static void ApplyOutcome(List<DriverState> states, string sourceUpdateId, UpdateStatus status)
    {
        var affected = states
            .Where(s => string.Equals(s.Candidate?.SourceUpdateId, sourceUpdateId, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        foreach (var state in affected)
        {
            switch (status)
            {
                case UpdateStatus.Succeeded:
                    state.Status = DriverStatus.UpToDate;
                    state.Candidate = null;
                    break;
                case UpdateStatus.Failed:
                    state.Status = DriverStatus.Error;
                    break;
                // Skipped / Cancelled: leave the candidate in place so the next run retries it.
            }
        }
    }

    private async Task SaveCacheAsync(List<DriverState> states, CancellationToken cancellationToken)
    {
        try
        {
            var entries = states
                .Select(s => new CachedDriverEntry(s.Driver, s.Status, s.Candidate))
                .ToArray();
            var snapshot = new DriverCacheSnapshot(DateTimeOffset.UtcNow, entries);
            await _driverCacheStore.SaveAsync(snapshot, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Scheduled run failed to save the driver cache");
        }
    }

    private static bool TryFind(Dictionary<string, DriverState> index, string hardwareId, out DriverState state)
    {
        if (!string.IsNullOrWhiteSpace(hardwareId) && index.TryGetValue(hardwareId, out var exact))
        {
            state = exact;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(hardwareId))
        {
            foreach (var (knownHardwareId, candidateState) in index)
            {
                if (DriverUpdateMatcher.IsBoundaryPrefix(knownHardwareId, hardwareId))
                {
                    state = candidateState;
                    return true;
                }
            }
        }

        state = null!;
        return false;
    }

    private static bool IsSourceDisabled(IUpdateSource source, UpdaterSettings settings) => source.Kind switch
    {
        UpdateSource.WindowsUpdate => !settings.WindowsUpdateEnabled,
        UpdateSource.Oem => !settings.OemSourcesEnabled,
        _ => false
    };

    private static bool IsInstallable(UpdateInstallKind kind) =>
        kind is UpdateInstallKind.WindowsUpdate
            or UpdateInstallKind.PnPUtilPackage
            or UpdateInstallKind.VendorInstaller;

    private sealed class DriverState
    {
        public DriverState(DriverInfo driver) => Driver = driver;

        public DriverInfo Driver { get; }
        public UpdateCandidate? Candidate { get; set; }
        public DriverStatus Status { get; set; } = DriverStatus.Unknown;
    }
}
