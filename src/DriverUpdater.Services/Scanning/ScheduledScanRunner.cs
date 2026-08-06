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
    private readonly IOptionsMonitor<ScheduleSettings>? _scheduleSettings;
    private readonly IAutoUpdateSelectionStore? _autoUpdateSelectionStore;
    private readonly IDriverUpdateExclusionStore? _exclusionStore;
    private readonly IAiAutoUpdateAdvisor? _aiAdvisor;
    private readonly ILogger<ScheduledScanRunner> _logger;

    public ScheduledScanRunner(
        IDriverScanService scanService,
        IEnumerable<IUpdateSource> updateSources,
        IInstallPipeline installPipeline,
        IDriverCacheStore driverCacheStore,
        IOptionsMonitor<UpdaterSettings> updaterSettings,
        ILogger<ScheduledScanRunner> logger,
        IOptionsMonitor<ScheduleSettings>? scheduleSettings = null,
        IAutoUpdateSelectionStore? autoUpdateSelectionStore = null,
        IAiAutoUpdateAdvisor? aiAdvisor = null,
        IDriverUpdateExclusionStore? exclusionStore = null)
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
        _scheduleSettings = scheduleSettings;
        _autoUpdateSelectionStore = autoUpdateSelectionStore;
        _exclusionStore = exclusionStore;
        _aiAdvisor = aiAdvisor;
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

        if (states.Count == 0)
        {
            _logger.LogWarning("Scheduled scan returned no drivers; preserving the previous cache");
            return;
        }

        var (exclusions, exclusionsAvailable) =
            await ResolveExclusionsAsync(cancellationToken).ConfigureAwait(false);
        MarkExcluded(states, exclusions);

        await QueryUpdateSourcesAsync(states, cancellationToken).ConfigureAwait(false);

        // Excluded devices are left out of the count on purpose: their candidate is kept only
        // so the next interactive session can show what was found, never to be installed.
        var outdated = states.Count(s => s.Candidate is not null && !s.IsExcluded);
        _logger.LogInformation("Scheduled scan matched {Count} installable update(s)", outdated);

        if (installUpdates && outdated > 0 && exclusionsAvailable)
        {
            var scope = await ResolveInstallScopeAsync(cancellationToken).ConfigureAwait(false);
            var aiApprovals = await ResolveAiApprovalsAsync(states, cancellationToken).ConfigureAwait(false);
            await InstallAsync(states, scope, aiApprovals, cancellationToken).ConfigureAwait(false);
        }
        else if (installUpdates && outdated > 0)
        {
            _logger.LogError(
                "Scheduled installs skipped because the excluded-driver list could not be read");
        }

        await CarryOverUnmatchedCacheEntriesAsync(states, cancellationToken).ConfigureAwait(false);
        await SaveCacheAsync(states, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Scheduled run completed");
    }

    private async Task QueryUpdateSourcesAsync(List<DriverState> states, CancellationToken cancellationToken)
    {
        // First state per hardware ID is the match target, mirroring the interactive grid.
        // Include compatible and secondary IDs because update sources do not always return
        // the same ID that WMI selected as the row's primary identifier.
        var index = new Dictionary<string, DriverState>(StringComparer.OrdinalIgnoreCase);
        foreach (var state in states)
        {
            foreach (var key in state.Driver.HardwareIds.Prepend(state.Driver.HardwareId))
            {
                if (!string.IsNullOrWhiteSpace(key) && !index.ContainsKey(key))
                {
                    index[key] = state;
                }
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
                        // An excluded device keeps the found version so the next interactive
                        // session can show it, but its status must not claim it is pending.
                        state.Status = state.IsExcluded ? DriverStatus.Excluded : DriverStatus.Outdated;
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

    // A scheduled run must fail closed when the exclusion list cannot be read. Treating a read
    // failure as an empty list would install updates for devices the user explicitly protected.
    private async Task<(DriverUpdateExclusions Exclusions, bool Available)> ResolveExclusionsAsync(
        CancellationToken cancellationToken)
    {
        if (_exclusionStore is null)
        {
            return (DriverUpdateExclusions.Empty, true);
        }

        try
        {
            var exclusions = await _exclusionStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (exclusions.DeviceIds.Count > 0)
            {
                _logger.LogInformation(
                    "Scheduled run ignores {Count} device(s) excluded from updates", exclusions.DeviceIds.Count);
            }
            return (exclusions, true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not read the excluded-driver list; scheduled installs are blocked");
            return (DriverUpdateExclusions.Empty, false);
        }
    }

    private static void MarkExcluded(List<DriverState> states, DriverUpdateExclusions exclusions)
    {
        if (exclusions.DeviceIds.Count == 0)
        {
            return;
        }

        foreach (var state in states.Where(s => exclusions.Contains(s.Driver.DeviceId)))
        {
            state.IsExcluded = true;
            state.Status = DriverStatus.Excluded;
        }
    }

    // Which devices this unattended run may install for. Null means every device: either the
    // user kept the default scope, or the settings/selection plumbing is not wired up (the
    // parameters are optional so older compositions keep their previous behaviour).
    private async Task<AutoUpdateSelection?> ResolveInstallScopeAsync(CancellationToken cancellationToken)
    {
        var scope = _scheduleSettings?.CurrentValue.AutoUpdateScope ?? AutoUpdateScope.AllDrivers;
        if (scope != AutoUpdateScope.SelectedDrivers || _autoUpdateSelectionStore is null)
        {
            return null;
        }

        try
        {
            var selection = await _autoUpdateSelectionStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Scheduled run installs only the {Count} driver(s) selected for automatic updates",
                selection.DeviceIds.Count);
            return selection;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Failing open would install drivers the user deliberately excluded, so treat an
            // unreadable selection as "nothing is opted in".
            _logger.LogError(ex, "Could not read the automatic-update selection; no driver is installed this run");
            return AutoUpdateSelection.Empty;
        }
    }

    // Which updates the AI endorsed for this run. Null means the user did not hand the choice
    // to the AI, so every candidate stays eligible. An empty set means the AI was supposed to
    // decide but could not: nothing is installed, because an unattended install the user asked
    // the AI to vet must never happen unvetted.
    private async Task<IReadOnlySet<string>?> ResolveAiApprovalsAsync(
        List<DriverState> states,
        CancellationToken cancellationToken)
    {
        var settings = _scheduleSettings?.CurrentValue;
        if ((settings?.AutoUpdateScope ?? AutoUpdateScope.AllDrivers) != AutoUpdateScope.AiRecommended)
        {
            return null;
        }

        var nothing = (IReadOnlySet<string>)new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_aiAdvisor is null || !_aiAdvisor.IsConfigured)
        {
            _logger.LogError(
                "Automatic updates are limited to what the AI recommends, but no AI provider is configured. Nothing is installed this run; pick a provider in Settings > AI.");
            return nothing;
        }

        var items = states
            .Where(s => !s.IsExcluded && s.Candidate is not null && IsInstallable(s.Candidate.InstallKind))
            .GroupBy(s => s.Candidate!.SourceUpdateId, StringComparer.OrdinalIgnoreCase)
            .Select(g => new AiUpdateReviewItem(g.First().Driver, g.First().Candidate!))
            .ToArray();
        if (items.Length == 0)
        {
            return nothing;
        }

        IReadOnlyList<AiUpdateDecision> decisions;
        try
        {
            decisions = await _aiAdvisor
                .ReviewAsync(items, settings!.AiRiskTolerance, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The AI review of this run's updates failed; no driver is installed");
            return nothing;
        }

        var approved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var decision in decisions)
        {
            if (decision.ShouldInstall)
            {
                approved.Add(decision.SourceUpdateId);
            }

            AttachVerdict(states, decision);
            _logger.LogInformation(
                "AI {Outcome} the update for {Device} (id={Id}): {Reason}",
                decision.ShouldInstall ? "approved" : "rejected",
                decision.DeviceName,
                decision.SourceUpdateId,
                decision.Reason);
        }

        _logger.LogInformation(
            "AI approved {Approved} of {Total} update(s) for unattended install at tolerance {Tolerance}",
            approved.Count, items.Length, settings!.AiRiskTolerance);
        return approved;
    }

    // Keep the AI's reasoning on the candidate so it survives into the saved cache and the
    // next interactive session can show why a scheduled run left an update alone.
    private static void AttachVerdict(List<DriverState> states, AiUpdateDecision decision)
    {
        if (decision.Verdict is null)
        {
            return;
        }

        foreach (var state in states)
        {
            if (state.Candidate is { } candidate
                && string.Equals(candidate.SourceUpdateId, decision.SourceUpdateId, StringComparison.OrdinalIgnoreCase))
            {
                state.Candidate = candidate with { AiVerification = decision.Verdict };
            }
        }
    }

    private async Task InstallAsync(
        List<DriverState> states,
        AutoUpdateSelection? scope,
        IReadOnlySet<string>? aiApprovals,
        CancellationToken cancellationToken)
    {
        var processed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skipped = 0;
        var rejectedByAi = 0;

        foreach (var state in states)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = state.Candidate;
            if (candidate is null || !IsInstallable(candidate.InstallKind))
            {
                continue;
            }
            // The device is on the user's exclusion list. Nothing should have attached a
            // candidate to it, but the guard stays here so a carried-over or shared-installer
            // path can never install for a device the user opted out of.
            if (state.IsExcluded)
            {
                continue;
            }
            // The user restricted automatic updates to specific devices. Filter before the
            // dedupe below so a shared installer is only ever reached through a device that
            // was actually opted in.
            if (scope is not null && !scope.Contains(state.Driver.DeviceId))
            {
                skipped++;
                continue;
            }
            // The user handed the choice to the AI: only what it endorsed gets installed.
            if (aiApprovals is not null && !aiApprovals.Contains(candidate.SourceUpdateId))
            {
                rejectedByAi++;
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

        if (skipped > 0)
        {
            _logger.LogInformation(
                "Scheduled run left {Count} available update(s) for the next interactive session: their device is not selected for automatic updates",
                skipped);
        }

        if (rejectedByAi > 0)
        {
            _logger.LogInformation(
                "Scheduled run left {Count} available update(s) for the next interactive session: the AI did not recommend installing them unattended",
                rejectedByAi);
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

    // The scheduled run queries a subset of what an interactive "Scan with AI" does, so saving
    // only what it found would delete every update the app is still offering the user - they
    // would disappear from the grid overnight. Carry those forward, but only into the saved
    // snapshot: this happens after the install pass, so an unattended run never installs a
    // candidate its own sources did not confirm.
    private async Task CarryOverUnmatchedCacheEntriesAsync(
        List<DriverState> states,
        CancellationToken cancellationToken)
    {
        var pendingStates = states.Where(s => s.Candidate is null).ToArray();
        if (pendingStates.Length == 0)
        {
            return;
        }

        DriverCacheSnapshot? snapshot;
        try
        {
            snapshot = await _driverCacheStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Scheduled run could not read the previous driver cache; nothing is carried over");
            return;
        }

        if (snapshot is null || snapshot.Entries.Count == 0)
        {
            return;
        }

        var previous = new Dictionary<string, UpdateCandidate>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in snapshot.Entries)
        {
            if (entry.AvailableUpdate is { } candidate
                && IsSafeCacheFallback(candidate)
                && !string.IsNullOrWhiteSpace(entry.Driver.DeviceId))
            {
                previous[entry.Driver.DeviceId] = candidate;
            }
        }

        var carriedOver = 0;
        foreach (var state in pendingStates)
        {
            if (previous.TryGetValue(state.Driver.DeviceId, out var pending)
                && pending.IsNewerThan(state.Driver))
            {
                state.Candidate = pending;
                state.Status = state.IsExcluded ? DriverStatus.Excluded : DriverStatus.Outdated;
                carriedOver++;
            }
        }

        if (carriedOver > 0)
        {
            _logger.LogInformation(
                "Scheduled run carried {Count} update(s) from the previous scan into the saved cache",
                carriedOver);
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
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

    private static bool IsSafeCacheFallback(UpdateCandidate candidate) =>
        candidate.Confidence == UpdateConfidence.Confirmed
        && candidate.InstallKind != UpdateInstallKind.VendorPage;

    private sealed class DriverState
    {
        public DriverState(DriverInfo driver) => Driver = driver;

        public DriverInfo Driver { get; }
        public UpdateCandidate? Candidate { get; set; }
        public DriverStatus Status { get; set; } = DriverStatus.Unknown;
        public bool IsExcluded { get; set; }
    }
}
