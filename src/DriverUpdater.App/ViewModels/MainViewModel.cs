using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DriverUpdater.App.Services;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using DriverUpdater.Core.Options;
using DriverUpdater.Services.Scanning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DriverUpdater.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private const int VendorVerificationAttempts = 20;
    private const int AiDiscoveryBatchSize = 20;
    private static readonly TimeSpan VendorVerificationInterval = TimeSpan.FromSeconds(3);

    private readonly IDriverScanService _scanService;
    private readonly IReadOnlyList<IUpdateSource> _updateSources;
    private readonly IOemDetectionService _oemDetectionService;
    private readonly IInstallPipeline _installPipeline;
    private readonly IInstallConfirmation _installConfirmation;
    private readonly IUpdatePageOpener? _updatePageOpener;
    private readonly IHistoryWindowOpener _historyWindowOpener;
    private readonly ISettingsWindowOpener _settingsWindowOpener;
    private readonly ILogsWindowOpener _logsWindowOpener;
    private readonly IAiResultWindowOpener? _aiResultWindowOpener;
    private readonly IDriverCacheStore? _driverCacheStore;
    private readonly IAiVerifier? _aiVerifier;
    private readonly IOptionsMonitor<UpdaterSettings>? _updaterSettings;
    private readonly IAppUpdater? _appUpdater;
    private readonly IAppUpdatePrompt? _appUpdatePrompt;
    private readonly IRebootPrompt? _rebootPrompt;
    private readonly IIneffectiveUpdateStore? _ineffectiveUpdateStore;

    // (DeviceId|TargetVersion) -> installed version when the update was last proven ineffective.
    // A candidate is suppressed while the device still reports that installed version.
    private Dictionary<string, string?> _ineffectiveIndex = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<MainViewModel> _logger;

    public ObservableCollection<DriverRowViewModel> Drivers { get; } = new();

    public ICollectionView DriversView { get; }

    public IReadOnlyList<DriverCategory> AvailableCategories { get; } =
        Enum.GetValues<DriverCategory>().ToArray();

    public IReadOnlyList<DriverUpdateFilterOption> AvailableUpdateFilters { get; } =
    [
        new(DriverUpdateFilter.All, "All updates"),
        new(DriverUpdateFilter.ConfirmedUpdates, "Confirmed"),
        new(DriverUpdateFilter.VendorChecks, "Vendor checks"),
        new(DriverUpdateFilter.Installable, "Installable"),
        new(DriverUpdateFilter.NoUpdate, "No update")
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    private string _statusText = "Ready. Click Scan to inventory drivers.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(AskAiAllCommand))]
    private bool _isScanning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    [NotifyCanExecuteChangedFor(nameof(AskAiAllCommand))]
    private int _scannedCount;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AskAiAllCommand))]
    private bool _isAskingAi;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    [NotifyCanExecuteChangedFor(nameof(UpdateOutdatedCommand))]
    [NotifyCanExecuteChangedFor(nameof(UpdateAllCommand))]
    [NotifyCanExecuteChangedFor(nameof(DryRunOutdatedCommand))]
    private int _updatesFoundCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    [NotifyCanExecuteChangedFor(nameof(InstallConfirmedCommand))]
    [NotifyCanExecuteChangedFor(nameof(UpdateAllCommand))]
    private int _confirmedUpdatesCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    [NotifyCanExecuteChangedFor(nameof(OpenVendorChecksCommand))]
    private int _vendorChecksCount;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpdateAppCommand))]
    private bool _isAppUpdateAvailable;

    [ObservableProperty]
    private string? _appUpdateVersion;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpdateAppCommand))]
    private bool _isAppUpdating;

    [ObservableProperty]
    private DriverCategory? _categoryFilter;

    [ObservableProperty]
    private DriverUpdateFilter _updateFilter = DriverUpdateFilter.All;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOem))]
    [NotifyCanExecuteChangedFor(nameof(OpenOemToolCommand))]
    private OemInfo? _detectedOem;

    public bool HasOem => DetectedOem is not null;

    public string ProgressText => IsScanning
        ? $"Scanning... {ScannedCount} drivers found"
        : ScannedCount > 0
            ? $"{ScannedCount} drivers ({ConfirmedUpdatesCount} confirmed, {VendorChecksCount} vendor checks)"
            : string.Empty;

    public MainViewModel(
        IDriverScanService scanService,
        IEnumerable<IUpdateSource> updateSources,
        IOemDetectionService oemDetectionService,
        IInstallPipeline installPipeline,
        IInstallConfirmation installConfirmation,
        IHistoryWindowOpener historyWindowOpener,
        ISettingsWindowOpener settingsWindowOpener,
        ILogsWindowOpener logsWindowOpener,
        ILogger<MainViewModel> logger,
        IUpdatePageOpener? updatePageOpener = null,
        IDriverCacheStore? driverCacheStore = null,
        IAiVerifier? aiVerifier = null,
        IOptionsMonitor<UpdaterSettings>? updaterSettings = null,
        IAiResultWindowOpener? aiResultWindowOpener = null,
        IAppUpdater? appUpdater = null,
        IAppUpdatePrompt? appUpdatePrompt = null,
        IRebootPrompt? rebootPrompt = null,
        IIneffectiveUpdateStore? ineffectiveUpdateStore = null)
    {
        ArgumentNullException.ThrowIfNull(scanService);
        ArgumentNullException.ThrowIfNull(updateSources);
        ArgumentNullException.ThrowIfNull(oemDetectionService);
        ArgumentNullException.ThrowIfNull(installPipeline);
        ArgumentNullException.ThrowIfNull(installConfirmation);
        ArgumentNullException.ThrowIfNull(historyWindowOpener);
        ArgumentNullException.ThrowIfNull(settingsWindowOpener);
        ArgumentNullException.ThrowIfNull(logsWindowOpener);
        ArgumentNullException.ThrowIfNull(logger);
        _scanService = scanService;
        _updateSources = updateSources.ToArray();
        _oemDetectionService = oemDetectionService;
        _installPipeline = installPipeline;
        _installConfirmation = installConfirmation;
        _updatePageOpener = updatePageOpener;
        _historyWindowOpener = historyWindowOpener;
        _settingsWindowOpener = settingsWindowOpener;
        _logsWindowOpener = logsWindowOpener;
        _aiResultWindowOpener = aiResultWindowOpener;
        _driverCacheStore = driverCacheStore;
        _aiVerifier = aiVerifier;
        _updaterSettings = updaterSettings;
        _appUpdater = appUpdater;
        _appUpdatePrompt = appUpdatePrompt;
        _rebootPrompt = rebootPrompt;
        _ineffectiveUpdateStore = ineffectiveUpdateStore;
        _logger = logger;

        DriversView = CollectionViewSource.GetDefaultView(Drivers);
        DriversView.Filter = FilterDriver;
    }

    partial void OnCategoryFilterChanged(DriverCategory? value) => DriversView.Refresh();
    partial void OnUpdateFilterChanged(DriverUpdateFilter value) => DriversView.Refresh();
    partial void OnSearchTextChanged(string value) => DriversView.Refresh();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            DetectedOem = await _oemDetectionService.DetectAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OEM detection failed");
        }

        await LoadDriverCacheAsync(cancellationToken).ConfigureAwait(true);

        await CheckForAppUpdateAsync(cancellationToken).ConfigureAwait(true);
    }

    private async Task CheckForAppUpdateAsync(CancellationToken cancellationToken)
    {
        if (_appUpdater is null)
        {
            return;
        }

        try
        {
            var result = await _appUpdater.CheckForUpdatesAsync(cancellationToken).ConfigureAwait(true);
            IsAppUpdateAvailable = result.IsUpdateAvailable;
            AppUpdateVersion = result.Version;
            if (!result.IsUpdateAvailable)
            {
                return;
            }

            _logger.LogInformation("App update {Version} is available", result.Version);
            StatusText = $"App update {result.Version} is available.";

            // Proactively offer to install it. The 'Update app' toolbar button stays
            // visible so the user can still update later if they decline now.
            if (_appUpdatePrompt is not null && _appUpdatePrompt.Confirm(result.Version))
            {
                await UpdateAppAsync(cancellationToken).ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Checking for an app update failed");
        }
    }

    [RelayCommand(CanExecute = nameof(CanUpdateApp))]
    private async Task UpdateAppAsync(CancellationToken cancellationToken)
    {
        if (_appUpdater is null)
        {
            return;
        }

        IsAppUpdating = true;
        StatusText = $"Downloading app update {AppUpdateVersion}...";
        try
        {
            var progress = new Progress<int>(percent =>
                StatusText = $"Downloading app update {AppUpdateVersion}... {percent}%");
            // On success the app downloads the new version and restarts immediately, so
            // execution does not return past this call.
            await _appUpdater.DownloadAndApplyAsync(progress, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Applying the app update failed");
            StatusText = $"App update failed: {ex.Message}";
        }
        finally
        {
            IsAppUpdating = false;
        }
    }

    private bool CanUpdateApp() => IsAppUpdateAvailable && !IsAppUpdating && _appUpdater is not null;

    private async Task LoadDriverCacheAsync(CancellationToken cancellationToken)
    {
        if (_driverCacheStore is null || Drivers.Count > 0)
        {
            return;
        }

        try
        {
            var snapshot = await _driverCacheStore.LoadAsync(cancellationToken).ConfigureAwait(true);
            if (snapshot is null || snapshot.Entries.Count == 0)
            {
                return;
            }

            var staleDropped = 0;
            foreach (var entry in snapshot.Entries)
            {
                // A cache written by an older build can hold an AvailableUpdate that our current
                // version comparison no longer considers an upgrade (e.g. a calendar-versioned
                // downgrade of a Windows inbox driver). Re-validate on load so the user cannot
                // install a stale downgrade straight from cache without re-scanning.
                var cachedUpdate = entry.AvailableUpdate;
                if (cachedUpdate is not null && !cachedUpdate.IsNewerThan(entry.Driver))
                {
                    cachedUpdate = null;
                    staleDropped++;
                }

                var row = new DriverRowViewModel(entry.Driver)
                {
                    Status = cachedUpdate is null && entry.Status == DriverStatus.Outdated
                        ? DriverStatus.UpToDate
                        : entry.Status,
                    AvailableUpdate = cachedUpdate
                };
                Drivers.Add(row);
            }

            if (staleDropped > 0)
            {
                _logger.LogInformation(
                    "Dropped {Count} cached update(s) that are no longer newer than the installed driver (stale downgrade guard).",
                    staleDropped);
            }

            ScannedCount = Drivers.Count;
            RefreshUpdateCounts();
            StatusText =
                $"Loaded {Drivers.Count} drivers from last scan on {snapshot.CapturedAt.LocalDateTime:yyyy-MM-dd HH:mm}. " +
                "Click Scan to refresh and check for updates.";
            _logger.LogInformation(
                "Loaded {Count} drivers from cache captured at {CapturedAt}",
                Drivers.Count, snapshot.CapturedAt);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load the driver cache");
        }
    }

    private async Task SaveDriverCacheAsync(CancellationToken cancellationToken)
    {
        if (_driverCacheStore is null)
        {
            return;
        }

        try
        {
            var entries = Drivers
                .Select(r => new CachedDriverEntry(r.Driver, r.Status, r.AvailableUpdate))
                .ToArray();
            var snapshot = new DriverCacheSnapshot(DateTimeOffset.UtcNow, entries);
            await _driverCacheStore.SaveAsync(snapshot, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save the driver cache");
        }
    }

    [RelayCommand(CanExecute = nameof(CanScan), IncludeCancelCommand = true)]
    private async Task ScanAsync(CancellationToken cancellationToken)
    {
        IsScanning = true;
        Drivers.Clear();
        ScannedCount = 0;
        UpdatesFoundCount = 0;
        ConfirmedUpdatesCount = 0;
        VendorChecksCount = 0;
        StatusText = "Scanning drivers via WMI...";
        var stopwatch = Stopwatch.StartNew();

        try
        {
            await foreach (var driver in _scanService.ScanAsync(cancellationToken))
            {
                Drivers.Add(new DriverRowViewModel(driver));
                ScannedCount = Drivers.Count;
            }

            var elapsed = stopwatch.Elapsed;
            StatusText = $"Scan complete. {Drivers.Count} drivers in {elapsed.TotalSeconds:F1}s. Querying update sources...";
            _logger.LogInformation("Scan finished: {Count} drivers in {Elapsed}", Drivers.Count, elapsed);

            await QueryUpdateSourcesAsync(cancellationToken);

            StatusText = $"Done. {Drivers.Count} drivers, {ConfirmedUpdatesCount} confirmed updates, {VendorChecksCount} vendor checks.";
            await SaveDriverCacheAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            StatusText = $"Scan cancelled. {Drivers.Count} drivers collected so far.";
            _logger.LogInformation("Scan cancelled");
        }
        catch (Exception ex)
        {
            StatusText = $"Scan failed: {ex.Message}";
            _logger.LogError(ex, "Scan failed");
        }
        finally
        {
            IsScanning = false;
        }
    }

    private async Task QueryUpdateSourcesAsync(CancellationToken cancellationToken)
    {
        if (Drivers.Count == 0)
        {
            return;
        }

        await LoadIneffectiveLedgerAsync(cancellationToken).ConfigureAwait(true);

        var index = BuildHardwareIdIndex();
        var driverSnapshots = Drivers.Select(d => d.Driver).ToArray();

        var settings = _updaterSettings?.CurrentValue;
        foreach (var source in _updateSources)
        {
            if (settings is not null && IsSourceDisabled(source, settings))
            {
                _logger.LogInformation("Skipping {Source}: disabled in settings", source.DisplayName);
                continue;
            }

            try
            {
                StatusText = $"Querying {source.DisplayName}...";
                _logger.LogInformation("Querying {Source}", source.DisplayName);

                await foreach (var candidate in source.SearchAsync(driverSnapshots, cancellationToken))
                {
                    if (TryFindRow(index, candidate.ForHardwareId, out var row, out var matchKind)
                        && candidate.IsNewerThan(row.Driver)
                        && !IsProvenIneffective(row, candidate)
                        && ShouldAcceptCandidate(row, candidate))
                    {
                        if (matchKind == HardwareIdMatchKind.Fuzzy)
                        {
                            _logger.LogWarning(
                                "{Source}: fuzzy prefix match - candidate ForHardwareId '{CandidateHwId}' bound to row '{RowDevice}' ({RowHwId}); download {Url}",
                                source.DisplayName, candidate.ForHardwareId, row.DeviceName, row.HardwareId, candidate.DownloadUrl);
                        }
                        row.AvailableUpdate = candidate;
                        row.Status = DriverStatus.Outdated;
                        RefreshUpdateCounts();
                    }
                }
            }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Source {Source} failed", source.DisplayName);
                    StatusText = $"{source.DisplayName} failed: {ex.Message}";
                }
        }

        await VerifyCandidatesWithAiAsync(cancellationToken).ConfigureAwait(true);
        await DiscoverLatestDriversWithAiAsync(onlyRowsWithoutUpdates: true, cancellationToken).ConfigureAwait(true);

        LogScanSummary();
    }

    private static string IneffectiveKey(string deviceId, string targetVersion) => deviceId + "|" + targetVersion;

    private async Task LoadIneffectiveLedgerAsync(CancellationToken cancellationToken)
    {
        if (_ineffectiveUpdateStore is null)
        {
            return;
        }

        try
        {
            var records = await _ineffectiveUpdateStore.LoadAsync(cancellationToken).ConfigureAwait(true);
            _ineffectiveIndex = records.ToDictionary(
                r => IneffectiveKey(r.DeviceId, r.TargetVersion),
                r => r.InstalledVersionAtAttempt,
                StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load the ineffective-update ledger; not suppressing any candidates");
            _ineffectiveIndex = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        }
    }

    // A candidate is a proven no-op when we previously installed this exact target for this device
    // and Windows kept the existing driver (no reboot pending), AND the device still reports the
    // same installed version - so re-installing would change nothing again. If the installed
    // version has since changed, the record no longer applies and the candidate is offered again.
    private bool IsProvenIneffective(DriverRowViewModel row, UpdateCandidate candidate)
    {
        if (_ineffectiveIndex.Count == 0 || candidate.NewVersion is null)
        {
            return false;
        }

        var key = IneffectiveKey(row.Driver.DeviceId, candidate.NewVersion.ToString());
        if (!_ineffectiveIndex.TryGetValue(key, out var installedAtAttempt))
        {
            return false;
        }

        var currentInstalled = row.Driver.CurrentVersion?.ToString();
        if (!string.Equals(installedAtAttempt, currentInstalled, StringComparison.OrdinalIgnoreCase))
        {
            return false; // something changed since the failed attempt - re-evaluate normally
        }

        _logger.LogInformation(
            "Suppressing {Device}: {Target} was already installed but Windows kept {Installed} (proven no-op). " +
            "It will be offered again if the installed driver changes or a newer version appears.",
            DriverDisplayName(row), candidate.NewVersion, currentInstalled ?? "the existing driver");
        return true;
    }

    private async Task RecordIfProvenIneffectiveAsync(DriverRowViewModel row, UpdateOperation finished, CancellationToken cancellationToken)
    {
        if (_ineffectiveUpdateStore is null || finished.Candidate.NewVersion is null)
        {
            return;
        }

        // Only record the proven immediate no-op: post-install verification saw the active driver
        // unchanged with no reboot pending (that is exactly the "kept the existing driver" skip).
        // Reboot-required successes are never recorded - they bind after a restart.
        var isProvenNoOp = finished.Status == UpdateStatus.Skipped
            && finished.ErrorMessage?.Contains("kept the existing driver", StringComparison.OrdinalIgnoreCase) == true;
        if (!isProvenNoOp)
        {
            return;
        }

        try
        {
            await _ineffectiveUpdateStore.RecordAsync(
                row.Driver.DeviceId,
                finished.Candidate.NewVersion.ToString(),
                finished.TargetSnapshot.CurrentVersion?.ToString(),
                cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not record the ineffective update for {Device}", DriverDisplayName(row));
        }
    }

    private static bool IsSourceDisabled(IUpdateSource source, UpdaterSettings settings) => source.Kind switch
    {
        UpdateSource.WindowsUpdate => !settings.WindowsUpdateEnabled,
        UpdateSource.Oem => !settings.OemSourcesEnabled,
        _ => false
    };

    // Best-effort post-scan pass. When an AI provider is configured it reviews every
    // candidate in one batched call to (1) suppress updates that are not genuinely
    // newer than what is installed and (2) annotate the rest with a risk assessment.
    // Any failure leaves the scan results exactly as they were.
    private async Task VerifyCandidatesWithAiAsync(CancellationToken cancellationToken)
    {
        if (_aiVerifier is null)
        {
            _logger.LogDebug("AI verification skipped: no verifier is registered");
            return;
        }
        if (!_aiVerifier.IsConfigured)
        {
            _logger.LogInformation(
                "AI verification skipped: provider {Provider} is not configured", _aiVerifier.Provider);
            return;
        }

        var targets = Drivers
            .Where(r => r.AvailableUpdate is { InstallKind: not UpdateInstallKind.VendorPage })
            .ToArray();
        if (targets.Length == 0)
        {
            _logger.LogInformation("AI verification skipped: no installable candidates to verify");
            return;
        }

        // Many rows can share one installer (e.g. an AMD chipset package that drives 18
        // device rows, all with the same SourceUpdateId). Send each installer to the AI
        // once - the verdict is attached back to every row that shares the id below.
        var requests = targets
            .GroupBy(r => r.AvailableUpdate!.SourceUpdateId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Select(BuildAiVerificationRequest)
            .ToArray();

        _logger.LogInformation(
            "AI verification: provider={Provider}, sending {Count} unique candidate(s) from {Rows} row(s)",
            _aiVerifier.Provider, requests.Length, targets.Length);
        foreach (var request in requests)
        {
            LogAiRequest("candidate verification", request);
        }

        var stopwatch = Stopwatch.StartNew();
        IReadOnlyDictionary<string, AiVerdict> verdicts;
        try
        {
            StatusText = "Verifying updates with AI...";
            verdicts = await _aiVerifier.VerifyAsync(requests, cancellationToken).ConfigureAwait(true);
            stopwatch.Stop();
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("AI verification cancelled after {ElapsedMs} ms", stopwatch.ElapsedMilliseconds);
            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogWarning(ex,
                "AI verification failed after {ElapsedMs} ms; leaving scan results unchanged",
                stopwatch.ElapsedMilliseconds);
            return;
        }

        if (verdicts.Count == 0)
        {
            _logger.LogWarning(
                "AI verification returned no verdicts after {ElapsedMs} ms; leaving all {Count} candidate(s) unchanged",
                stopwatch.ElapsedMilliseconds, requests.Length);
            StatusText = "AI verification returned no usable result; scan results unchanged.";
            return;
        }

        var suppressed = 0;
        var annotated = 0;
        var withoutVerdict = 0;
        foreach (var row in targets)
        {
            var candidate = row.AvailableUpdate;
            if (candidate is null)
            {
                continue;
            }
            if (!verdicts.TryGetValue(candidate.SourceUpdateId, out var verdict))
            {
                withoutVerdict++;
                _logger.LogDebug(
                    "AI returned no verdict for {Device} (id={Id}); leaving it as-is",
                    row.DeviceName, candidate.SourceUpdateId);
                continue;
            }

            if (ApplyAiVerdict(row, verdict))
            {
                annotated++;
            }
            else
            {
                suppressed++;
            }
        }

        RefreshUpdateCounts();
        _logger.LogInformation(
            "AI verification applied in {ElapsedMs} ms: {Suppressed} suppressed, {Annotated} annotated, {WithoutVerdict} left untouched (no verdict)",
            stopwatch.ElapsedMilliseconds, suppressed, annotated, withoutVerdict);
        StatusText = $"AI verification complete. {suppressed} suppressed, {annotated} annotated.";
    }

    [RelayCommand]
    private async Task AskAiAsync(DriverRowViewModel? row, CancellationToken cancellationToken)
    {
        if (row is null)
        {
            StatusText = "No driver selected for AI review.";
            return;
        }
        if (row.IsAiChecking)
        {
            return;
        }
        if (_aiVerifier is null)
        {
            StatusText = "AI review is not available in this build.";
            return;
        }
        if (!_aiVerifier.IsConfigured)
        {
            StatusText = $"AI review is not configured. Open Settings > AI to enable {_aiVerifier.Provider}.";
            return;
        }

        row.IsAiChecking = true;
        try
        {
            var hasCandidate = row.AvailableUpdate is not null;
            _logger.LogInformation(
                "Ask AI single-row started: mode={Mode}, device={Device}, hardwareId={HardwareId}, installed={Installed}, candidate={Candidate}",
                hasCandidate ? "candidate-verification" : "latest-driver-discovery",
                row.DeviceName,
                row.HardwareId,
                row.Driver.CurrentVersion?.ToString() ?? "unknown",
                row.AvailableUpdate?.NewVersion.ToString() ?? "(none)");
            StatusText = hasCandidate
                ? $"Asking AI about {row.DeviceName}..."
                : $"Asking AI to find the latest driver for {row.DeviceName}...";
            var request = hasCandidate
                ? BuildAiVerificationRequest(row)
                : BuildAiDiscoveryRequest(row);
            LogAiRequest("single-row Ask AI", request);
            var verdicts = await _aiVerifier.VerifyAsync(new[] { request }, cancellationToken).ConfigureAwait(true);
            if (!verdicts.TryGetValue(request.CorrelationId, out var verdict))
            {
                _logger.LogWarning(
                    "Ask AI single-row returned no verdict: id={Id}, device={Device}, mode={Mode}",
                    request.CorrelationId,
                    row.DeviceName,
                    hasCandidate ? "candidate-verification" : "latest-driver-discovery");
                StatusText = hasCandidate
                    ? "AI did not return a usable recommendation for this update."
                    : "AI did not return a usable latest-driver result.";
                return;
            }

            var candidateForWindow = row.AvailableUpdate;
            var kept = hasCandidate
                ? ApplyAiVerdict(row, verdict)
                : ApplyAiDiscoveryVerdict(row, verdict);
            RefreshUpdateCounts();
            _aiResultWindowOpener?.Open(row.Driver, candidateForWindow ?? row.AvailableUpdate, verdict);
            if (hasCandidate)
            {
                StatusText = kept
                    ? $"AI recommendation for {row.DeviceName}: {row.AiRecommendationText} ({row.AiRiskText})."
                    : $"AI does not recommend this update for {row.DeviceName}; it was removed from available updates.";
            }
            else
            {
                StatusText = kept
                    ? $"AI found a newer driver for {row.DeviceName}: {row.AvailableVersionText}. Open the vendor check to continue."
                    : $"AI did not find a newer official driver for {row.DeviceName}.";
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "AI review cancelled.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AI single-update review failed for {Device}", row.DeviceName);
            StatusText = $"AI review failed: {ex.Message}";
        }
        finally
        {
            row.IsAiChecking = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanAskAiAll))]
    private async Task AskAiAllAsync(CancellationToken cancellationToken)
    {
        if (_aiVerifier is null)
        {
            StatusText = "AI review is not available in this build.";
            return;
        }
        if (!_aiVerifier.IsConfigured)
        {
            StatusText = $"AI review is not configured. Open Settings > AI to enable {_aiVerifier.Provider}.";
            return;
        }
        if (Drivers.Count == 0)
        {
            StatusText = "Scan drivers before asking AI to check all of them.";
            return;
        }

        IsAskingAi = true;
        _logger.LogInformation(
            "Ask AI all started: rows={Rows}, existingCandidates={ExistingCandidates}, rowsWithoutUpdates={RowsWithoutUpdates}, provider={Provider}",
            Drivers.Count,
            Drivers.Count(r => r.AvailableUpdate is not null),
            Drivers.Count(r => r.AvailableUpdate is null),
            _aiVerifier.Provider);
        try
        {
            await VerifyCandidatesWithAiAsync(cancellationToken).ConfigureAwait(true);
            await DiscoverLatestDriversWithAiAsync(onlyRowsWithoutUpdates: true, cancellationToken).ConfigureAwait(true);
            await SaveDriverCacheAsync(cancellationToken).ConfigureAwait(true);
            _logger.LogInformation(
                "Ask AI all completed: rows={Rows}, installableUpdates={InstallableUpdates}, vendorChecks={VendorChecks}, confirmed={Confirmed}",
                Drivers.Count,
                UpdatesFoundCount,
                VendorChecksCount,
                ConfirmedUpdatesCount);
        }
        catch (OperationCanceledException)
        {
            StatusText = "AI review cancelled.";
            _logger.LogInformation("Ask AI all cancelled");
        }
        finally
        {
            IsAskingAi = false;
        }
    }

    private bool CanAskAiAll() => Drivers.Count > 0 && !IsScanning && !IsAskingAi;

    private async Task DiscoverLatestDriversWithAiAsync(bool onlyRowsWithoutUpdates, CancellationToken cancellationToken)
    {
        if (_aiVerifier is null)
        {
            _logger.LogDebug("AI latest-driver discovery skipped: no verifier is registered");
            return;
        }
        if (!_aiVerifier.IsConfigured)
        {
            _logger.LogInformation(
                "AI latest-driver discovery skipped: provider {Provider} is not configured", _aiVerifier.Provider);
            return;
        }

        var targets = Drivers
            .Where(r => !onlyRowsWithoutUpdates || r.AvailableUpdate is null)
            .ToArray();
        if (targets.Length == 0)
        {
            _logger.LogInformation("AI latest-driver discovery skipped: no rows need discovery");
            return;
        }

        var found = 0;
        var noNewer = 0;
        var withoutVerdict = 0;
        var failedBatches = 0;
        var processed = 0;

        foreach (var batch in targets.Chunk(AiDiscoveryBatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var row in batch)
            {
                row.IsAiChecking = true;
            }

            try
            {
                var requests = batch.Select(BuildAiDiscoveryRequest).ToArray();
                StatusText =
                    $"Asking AI to find latest drivers... {processed + 1}-{processed + batch.Length} of {targets.Length}";
                _logger.LogInformation(
                    "AI latest-driver discovery: provider={Provider}, sending batch {Start}-{End} of {Total}",
                    _aiVerifier.Provider, processed + 1, processed + batch.Length, targets.Length);
                foreach (var request in requests)
                {
                    LogAiRequest("latest-driver discovery", request);
                }

                var verdicts = await _aiVerifier.VerifyAsync(requests, cancellationToken).ConfigureAwait(true);
                foreach (var row in batch)
                {
                    var id = BuildAiDiscoveryCorrelationId(row);
                    if (!verdicts.TryGetValue(id, out var verdict))
                    {
                        withoutVerdict++;
                        _logger.LogWarning(
                            "AI latest-driver discovery returned no verdict for {Device} (id={Id}, hardwareId={HardwareId})",
                            row.DeviceName, id, row.HardwareId);
                        continue;
                    }

                    if (ApplyAiDiscoveryVerdict(row, verdict))
                    {
                        found++;
                    }
                    else
                    {
                        noNewer++;
                    }
                }
                RefreshUpdateCounts();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failedBatches++;
                _logger.LogWarning(ex, "AI latest-driver discovery batch failed");
            }
            finally
            {
                foreach (var row in batch)
                {
                    row.IsAiChecking = false;
                }
                processed += batch.Length;
            }
        }

        StatusText =
            $"AI latest-driver search complete. {found} vendor checks found, {noNewer} already current, {withoutVerdict} no result."
            + (failedBatches > 0 ? $" {failedBatches} batch(es) failed." : string.Empty);
        _logger.LogInformation(
            "AI latest-driver discovery complete: targets={Targets}, found={Found}, noNewer={NoNewer}, withoutVerdict={WithoutVerdict}, failedBatches={FailedBatches}",
            targets.Length, found, noNewer, withoutVerdict, failedBatches);
    }

    private static AiVerificationRequest BuildAiDiscoveryRequest(DriverRowViewModel row) =>
        new(
            CorrelationId: BuildAiDiscoveryCorrelationId(row),
            DeviceName: row.DeviceName,
            HardwareId: row.HardwareId,
            InstalledVersion: row.Driver.CurrentVersion?.ToString(),
            InstalledDate: row.Driver.CurrentDate,
            CandidateVersion: "latest available",
            CandidateDate: DateOnly.FromDateTime(DateTime.UtcNow),
            Source: UpdateSource.Oem,
            DownloadUrl: BuildSearchUrl(row).AbsoluteUri,
            Category: row.Driver.Category,
            Provider: row.Driver.Provider,
            Manufacturer: row.Driver.Manufacturer,
            InstallKind: UpdateInstallKind.VendorPage,
            Confidence: UpdateConfidence.Advisory,
            FindLatestWhenNoCandidate: true);

    private static AiVerificationRequest BuildAiVerificationRequest(DriverRowViewModel row) =>
        new(
            CorrelationId: row.AvailableUpdate!.SourceUpdateId,
            DeviceName: row.DeviceName,
            HardwareId: row.HardwareId,
            InstalledVersion: row.Driver.CurrentVersion?.ToString(),
            InstalledDate: row.Driver.CurrentDate,
            CandidateVersion: row.AvailableUpdate.NewVersion.ToString(),
            CandidateDate: row.AvailableUpdate.NewDate,
            Source: row.AvailableUpdate.Source,
            DownloadUrl: row.AvailableUpdate.DownloadUrl.AbsoluteUri,
            Category: row.Driver.Category,
            Provider: row.Driver.Provider,
            Manufacturer: row.Driver.Manufacturer,
            InstallKind: row.AvailableUpdate.InstallKind,
            Confidence: row.AvailableUpdate.Confidence);

    private void LogAiRequest(string feature, AiVerificationRequest request)
    {
        _logger.LogDebug(
            "AI request [{Feature}]: id={Id}, mode={Mode}, device={Device}, hardwareId={HardwareId}, category={Category}, provider={Provider}, manufacturer={Manufacturer}, installed={Installed} ({InstalledDate}), candidate={Candidate} ({CandidateDate}), source={Source}, installKind={InstallKind}, confidence={Confidence}, url={Url}",
            feature,
            request.CorrelationId,
            request.FindLatestWhenNoCandidate ? "latest-driver-discovery" : "candidate-verification",
            request.DeviceName,
            request.HardwareId,
            request.Category,
            request.Provider,
            request.Manufacturer,
            request.InstalledVersion ?? "unknown",
            request.InstalledDate?.ToString("yyyy-MM-dd") ?? "unknown",
            request.CandidateVersion,
            request.CandidateDate.ToString("yyyy-MM-dd"),
            request.Source,
            request.InstallKind,
            request.Confidence,
            request.DownloadUrl);
    }

    private bool ApplyAiDiscoveryVerdict(DriverRowViewModel row, AiVerdict verdict)
    {
        if (!verdict.IsGenuinelyNewer)
        {
            _logger.LogInformation(
                "AI latest-driver search found no newer official driver for {Device}. summary={Summary}; recommended={Recommended}; installedSuitability={InstalledSuitability}; advisorNote={AdvisorNote}",
                row.DeviceName,
                verdict.Summary,
                verdict.RecommendedVersion ?? "(none)",
                verdict.InstalledSuitability ?? "(none)",
                verdict.AdvisorNote ?? "(none)");
            LogAiAdvisorDetails("latest-driver discovery kept current", row, verdict);
            return false;
        }

        var candidateVersion = TryParseDriverVersion(verdict.LatestKnownVersion)
            ?? BuildDateBasedVersion(verdict.LatestKnownDate ?? DateOnly.FromDateTime(DateTime.UtcNow));
        var candidateDate = verdict.LatestKnownDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var url = TryCreateAbsoluteUri(verdict.LatestKnownUrl) ?? BuildSearchUrl(row);
        if (!IsActionableAiDiscoveryLead(row, url))
        {
            _logger.LogInformation(
                "AI latest-driver search returned advisory-only result for {Device}: latest={Latest}, url={Url}. No vendor check was created because the URL/device is not an actionable driver update lead. {Summary}",
                row.DeviceName,
                verdict.LatestKnownVersion ?? candidateVersion.ToString(),
                url,
                verdict.Summary);
            LogAiAdvisorDetails("latest-driver discovery advisory-only", row, verdict);
            return false;
        }

        var candidate = new UpdateCandidate(
            ForHardwareId: row.HardwareId,
            Source: UpdateSource.Oem,
            NewVersion: candidateVersion,
            NewDate: candidateDate,
            DownloadUrl: url,
            SizeBytes: 0,
            KbArticle: null,
            IsSuperseded: false,
            SourceUpdateId: BuildAiDiscoveryCorrelationId(row),
            SupersededIds: Array.Empty<string>(),
            InstallKind: UpdateInstallKind.VendorPage,
            Confidence: UpdateConfidence.Advisory,
            AiVerification: verdict);

        // Deterministic downgrade guard. The AI's own "genuinely newer" judgment - and the
        // date-based version fallback above - can be wrong: e.g. proposing a calendar-versioned
        // 2018/2021 driver over a modern Windows inbox driver (10.0.26100.x). This discovery
        // path bypasses the IsNewerThan check that the catalog/vendor sources go through, so
        // apply it here too. Without this, the AI can reintroduce exactly the downgrades the
        // deterministic sources already rejected.
        if (!candidate.IsNewerThan(row.Driver))
        {
            _logger.LogInformation(
                "AI latest-driver lead for {Device} rejected: proposed {Candidate} ({Date}) is not newer than " +
                "installed {Installed} per version comparison - refusing to avoid a downgrade.",
                row.DeviceName, candidateVersion, candidateDate,
                row.Driver.CurrentVersion?.ToString() ?? row.Driver.CurrentDate?.ToString() ?? "unknown");
            LogAiAdvisorDetails("latest-driver discovery rejected as not-newer", row, verdict);
            return false;
        }

        _logger.LogInformation(
            "AI latest-driver search found {Device}: latest={Latest} ({Date}), recommended={Recommended}, risk={Risk}, url={Url}. {Summary}",
            row.DeviceName, verdict.LatestKnownVersion ?? candidateVersion.ToString(),
            candidateDate,
            verdict.RecommendedVersion ?? "(none)",
            verdict.Risk,
            url,
            verdict.Summary);
        LogAiAdvisorDetails("latest-driver discovery found candidate", row, verdict);
        row.AvailableUpdate = candidate;
        row.Status = DriverStatus.UpToDate;
        return true;
    }

    private bool ApplyAiVerdict(DriverRowViewModel row, AiVerdict verdict)
    {
        var candidate = row.AvailableUpdate;
        if (candidate is null)
        {
            return false;
        }

        if (!verdict.IsGenuinelyNewer)
        {
            _logger.LogInformation(
                "AI suppressed {Device}: not genuinely newer than installed {Installed} (risk={Risk}, recommended={Recommended}). {Summary}",
                row.DeviceName,
                row.Driver.CurrentVersion?.ToString() ?? "unknown",
                verdict.Risk,
                verdict.RecommendedVersion ?? "(none)",
                verdict.Summary);
            LogAiAdvisorDetails("candidate verification suppressed", row, verdict);
            row.AvailableUpdate = null;
            row.Status = DriverStatus.UpToDate;
            return false;
        }

        _logger.LogInformation(
            "AI reviewed {Device}: recommendation={Recommendation}, risk={Risk}, latestKnown={Latest}, recommended={Recommended}. {Summary}",
            row.DeviceName,
            verdict.Summary,
            verdict.Risk,
            verdict.LatestKnownVersion ?? "unknown",
            verdict.RecommendedVersion ?? "(none)",
            verdict.Summary);
        LogAiAdvisorDetails("candidate verification annotated", row, verdict);
        row.AvailableUpdate = candidate with { AiVerification = verdict };
        return true;
    }

    private void LogAiAdvisorDetails(string feature, DriverRowViewModel row, AiVerdict verdict)
    {
        _logger.LogDebug(
            "AI advisor [{Feature}] for {Device}: installedSuitability={InstalledSuitability}; candidateSuitability={CandidateSuitability}; recommendedVersion={RecommendedVersion}; latestKnown={LatestKnown}; latestDate={LatestDate}; latestUrl={LatestUrl}; advisorNote={AdvisorNote}; rationale={Rationale}",
            feature,
            row.DeviceName,
            verdict.InstalledSuitability ?? "(none)",
            verdict.CandidateSuitability ?? "(none)",
            verdict.RecommendedVersion ?? "(none)",
            verdict.LatestKnownVersion ?? "(none)",
            verdict.LatestKnownDate?.ToString("yyyy-MM-dd") ?? "(none)",
            verdict.LatestKnownUrl ?? "(none)",
            verdict.AdvisorNote ?? "(none)",
            verdict.Rationale);
    }

    private static string BuildAiDiscoveryCorrelationId(DriverRowViewModel row)
    {
        var id = !string.IsNullOrWhiteSpace(row.HardwareId)
            ? row.HardwareId
            : !string.IsNullOrWhiteSpace(row.Driver.DeviceId)
                ? row.Driver.DeviceId
                : row.DeviceName;
        return "ai-latest:" + id;
    }

    private static Uri BuildSearchUrl(DriverRowViewModel row)
    {
        var query = string.Join(
            " ",
            new[]
            {
                row.Provider,
                row.Manufacturer,
                row.DeviceName,
                row.HardwareId,
                "driver download"
            }.Where(s => !string.IsNullOrWhiteSpace(s)));
        return new Uri("https://www.google.com/search?q=" + Uri.EscapeDataString(query));
    }

    private static Uri? TryCreateAbsoluteUri(string? raw) =>
        Uri.TryCreate(raw, UriKind.Absolute, out var uri) ? uri : null;

    private static bool IsActionableAiDiscoveryLead(DriverRowViewModel row, Uri url)
    {
        if (!url.IsAbsoluteUri)
        {
            return false;
        }

        var host = url.Host.ToLowerInvariant();
        if (host is "www.google.com" or "google.com" or "learn.microsoft.com" or "docs.microsoft.com")
        {
            return false;
        }

        if (host.EndsWith(".google.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (IsMicrosoftInboxVirtualDriver(row)
            && !host.Contains("catalog.update.microsoft.com", StringComparison.OrdinalIgnoreCase)
            && !host.Contains("download.microsoft.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static bool IsMicrosoftInboxVirtualDriver(DriverRowViewModel row)
    {
        var isMicrosoft = Contains(row.Provider, "Microsoft") || Contains(row.Manufacturer, "Microsoft");
        if (!isMicrosoft)
        {
            return false;
        }

        return row.HardwareId.StartsWith("SWD\\", StringComparison.OrdinalIgnoreCase)
            || row.HardwareId.StartsWith("ROOT\\", StringComparison.OrdinalIgnoreCase)
            || row.HardwareId.StartsWith("HTREE\\", StringComparison.OrdinalIgnoreCase)
            || row.DeviceName.Contains("Generic software device", StringComparison.OrdinalIgnoreCase)
            || row.DeviceName.Contains("Generic", StringComparison.OrdinalIgnoreCase) && row.Category == DriverCategory.System;
    }

    private static Version? TryParseDriverVersion(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var start = -1;
        for (var i = 0; i < raw.Length; i++)
        {
            if (char.IsDigit(raw[i]))
            {
                start = i;
                break;
            }
        }
        if (start < 0)
        {
            return null;
        }

        var end = start;
        while (end < raw.Length && (char.IsDigit(raw[end]) || raw[end] == '.'))
        {
            end++;
        }

        var versionText = raw[start..end].Trim('.');
        var parts = versionText.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return null;
        }
        if (parts.Length > 4)
        {
            versionText = string.Join('.', parts.Take(4));
        }

        return Version.TryParse(versionText, out var version) ? version : null;
    }

    private static Version BuildDateBasedVersion(DateOnly date) =>
        new(date.Year, date.Month, date.Day, 0);

    private Dictionary<string, List<DriverRowViewModel>> BuildHardwareIdIndex()
    {
        var dict = new Dictionary<string, List<DriverRowViewModel>>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Drivers)
        {
            var keys = row.Driver.HardwareIds.Count > 0 ? row.Driver.HardwareIds : new[] { row.HardwareId };
            foreach (var key in keys.Where(k => !string.IsNullOrWhiteSpace(k)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!dict.TryGetValue(key, out var bucket))
                {
                    bucket = new List<DriverRowViewModel>();
                    dict[key] = bucket;
                }
                if (!bucket.Contains(row))
                {
                    bucket.Add(row);
                }
            }
        }
        return dict;
    }

    internal enum HardwareIdMatchKind
    {
        None,
        Exact,
        Fuzzy
    }

    private static bool TryFindRow(
        Dictionary<string, List<DriverRowViewModel>> index,
        string hardwareId,
        out DriverRowViewModel row,
        out HardwareIdMatchKind matchKind)
    {
        if (!string.IsNullOrWhiteSpace(hardwareId) && index.TryGetValue(hardwareId, out var bucket) && bucket.Count > 0)
        {
            row = bucket[0];
            matchKind = HardwareIdMatchKind.Exact;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(hardwareId))
        {
            foreach (var (knownHardwareId, rows) in index)
            {
                if (rows.Count > 0 && IsBoundaryPrefix(knownHardwareId, hardwareId))
                {
                    row = rows[0];
                    matchKind = HardwareIdMatchKind.Fuzzy;
                    return true;
                }
            }
        }

        row = null!;
        matchKind = HardwareIdMatchKind.None;
        return false;
    }

    // Delegates to the shared matcher so the interactive scan and the headless scheduled
    // scan agree on hardware-ID matching. Kept here as the tested entry point.
    internal static bool IsBoundaryPrefix(string a, string b) =>
        DriverUpdateMatcher.IsBoundaryPrefix(a, b);

    private void RefreshUpdateCounts()
    {
        UpdatesFoundCount = Drivers.Count(d => d.Status == DriverStatus.Outdated);
        ConfirmedUpdatesCount = Drivers.Count(d => d.AvailableUpdate?.Confidence == UpdateConfidence.Confirmed);
        VendorChecksCount = Drivers.Count(d => d.AvailableUpdate?.Confidence == UpdateConfidence.Advisory);
    }

    private static bool ShouldAcceptCandidate(DriverRowViewModel row, UpdateCandidate candidate) =>
        DriverUpdateMatcher.ShouldReplace(row.AvailableUpdate, candidate);

    private bool CanScan() => !IsScanning;

    [RelayCommand]
    private void OpenHistory()
    {
        _historyWindowOpener.Open();
    }

    [RelayCommand]
    private void OpenSettings()
    {
        _settingsWindowOpener.Open();
    }

    [RelayCommand]
    private void OpenLogs()
    {
        _logsWindowOpener.Open();
    }

    [RelayCommand]
    private void Clear()
    {
        Drivers.Clear();
        ScannedCount = 0;
        UpdatesFoundCount = 0;
        ConfirmedUpdatesCount = 0;
        VendorChecksCount = 0;
        StatusText = "Cleared.";
    }

    [RelayCommand(CanExecute = nameof(CanRunAnyUpdates))]
    private async Task UpdateOutdatedAsync(CancellationToken cancellationToken)
    {
        await RunUpdatesAsync(Drivers, dryRun: false, includeVendorPages: true, cancellationToken).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanUpdateAll))]
    private async Task UpdateAllAsync(CancellationToken cancellationToken)
    {
        await RunUpdatesAsync(Drivers, dryRun: false, includeVendorPages: true, cancellationToken).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task UpdateSelectedAsync(IList? selection, CancellationToken cancellationToken)
    {
        if (selection is null)
        {
            StatusText = "No rows selected.";
            return;
        }

        var rows = selection.OfType<DriverRowViewModel>().ToArray();
        if (rows.Length == 0)
        {
            StatusText = "No rows selected.";
            return;
        }

        await RunUpdatesAsync(rows, dryRun: false, includeVendorPages: true, cancellationToken).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task UpdateSingleAsync(DriverRowViewModel? row, CancellationToken cancellationToken)
    {
        if (row is null)
        {
            return;
        }

        await RunUpdatesAsync(new[] { row }, dryRun: false, includeVendorPages: true, cancellationToken).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanRunAnyUpdates))]
    private async Task DryRunOutdatedAsync(CancellationToken cancellationToken)
    {
        await RunUpdatesAsync(Drivers, dryRun: true, includeVendorPages: false, cancellationToken).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanInstallConfirmed))]
    private async Task InstallConfirmedAsync(CancellationToken cancellationToken)
    {
        await RunUpdatesAsync(Drivers, dryRun: false, includeVendorPages: false, cancellationToken).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanOpenVendorChecks))]
    private async Task OpenVendorChecksAsync(CancellationToken cancellationToken)
    {
        var pageTargets = Drivers
            .Where(r => r.AvailableUpdate is { InstallKind: UpdateInstallKind.VendorPage })
            .ToArray();

        if (pageTargets.Length == 0)
        {
            StatusText = "No vendor checks to open.";
            return;
        }

        await OpenVendorPagesAsync(pageTargets, cancellationToken).ConfigureAwait(true);
        StatusText = $"Opened {pageTargets.Length} vendor update pages.";
    }

    private bool CanRunAnyUpdates() => UpdatesFoundCount > 0;

    private bool CanUpdateAll() => UpdatesFoundCount > 0;

    private bool CanInstallConfirmed() => ConfirmedUpdatesCount > 0;

    private bool CanOpenVendorChecks() => VendorChecksCount > 0 && _updatePageOpener is not null;

    public event EventHandler<DriverRowViewModel>? ScrollToRowRequested;

    private async Task RunUpdatesAsync(
        IEnumerable<DriverRowViewModel> requested,
        bool dryRun,
        bool includeVendorPages,
        CancellationToken cancellationToken)
    {
        var targets = requested
            .Where(r => r.AvailableUpdate is not null)
            .ToArray();

        if (targets.Length == 0)
        {
            StatusText = "No outdated drivers to update.";
            return;
        }

        var installTargets = targets
            .Where(r => r.Status == DriverStatus.Outdated
                && r.AvailableUpdate is { InstallKind: UpdateInstallKind.WindowsUpdate or UpdateInstallKind.PnPUtilPackage or UpdateInstallKind.VendorInstaller })
            .ToArray();
        var pageTargets = targets
            .Where(r => r.Status == DriverStatus.Outdated
                && r.AvailableUpdate is { InstallKind: UpdateInstallKind.VendorPage })
            .ToArray();

        // Vendor page rows go through the pipeline too: it tries to resolve a direct
        // installer from the page and install silently. Only when that fails does the
        // pipeline report Skipped and the row falls back to opening the page below.
        if (!dryRun && includeVendorPages && pageTargets.Length > 0)
        {
            installTargets = installTargets.Concat(pageTargets).ToArray();
        }

        if (installTargets.Length == 0)
        {
            StatusText = dryRun
                ? $"Dry run completed. {pageTargets.Length} vendor pages would be opened."
                : "No confirmed updates to install.";
            return;
        }

        var firstTarget = installTargets[0];
        var sampleOperation = UpdateOperation.NewPending(firstTarget.AvailableUpdate!, firstTarget.Driver);
        var confirmResult = _installConfirmation.Confirm(sampleOperation, dryRun);
        if (confirmResult is null)
        {
            StatusText = "Update cancelled.";
            return;
        }
        var options = confirmResult;

        // Switch the grid to show only installable rows so the user does not have
        // to scrub through 250 unrelated entries to follow the active driver. The
        // user can pick a different filter later; we leave it on Installable when
        // the run finishes so the result of each install (UpToDate / Failed) is
        // visible at a glance.
        UpdateFilter = DriverUpdateFilter.Installable;

        var runStartedAt = DateTimeOffset.UtcNow;
        var processedUpdateIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var outcomes = new List<(DriverRowViewModel Row, UpdateOperation Operation)>();
        var skipped = new List<(DriverRowViewModel Row, string Reason)>();
        var vendorPageFallbacks = new List<DriverRowViewModel>();
        foreach (var row in installTargets)
        {
            if (row.AvailableUpdate is null)
            {
                _logger.LogInformation(
                    "Update run: skipping {Device} - candidate was already cleared by an earlier shared install",
                    DriverDisplayName(row));
                skipped.Add((row, "candidate was already cleared by an earlier shared install"));
                continue;
            }
            if (!processedUpdateIds.Add(row.AvailableUpdate.SourceUpdateId))
            {
                _logger.LogInformation(
                    "Update run: skipping {Device} - deduplicated, same installer as a previous row ({SourceUpdateId})",
                    DriverDisplayName(row), row.AvailableUpdate.SourceUpdateId);
                skipped.Add((row, $"deduplicated - same installer as a previous row ({row.AvailableUpdate.SourceUpdateId})"));
                continue;
            }
            cancellationToken.ThrowIfCancellationRequested();

            var originalUpdateId = row.AvailableUpdate.SourceUpdateId;
            var displayName = DriverDisplayName(row);
            var op = UpdateOperation.NewPending(row.AvailableUpdate, row.Driver);
            row.ActiveOperation = op;
            StatusText = (dryRun ? "Dry run: " : "Installing: ") + displayName;
            ScrollToRowRequested?.Invoke(this, row);
            _logger.LogInformation(
                "Update run: starting {Device} (current version={CurrentVersion}, target version={TargetVersion}, source={Source}, kind={Kind}, url={Url})",
                displayName, row.Driver.CurrentVersion, row.AvailableUpdate.NewVersion,
                row.AvailableUpdate.Source, row.AvailableUpdate.InstallKind, row.AvailableUpdate.DownloadUrl);

            var finished = await _installPipeline.ExecuteAsync(op, options, new Progress<UpdateOperation>(report =>
            {
                row.ActiveOperation = report;
                row.Status = MapOperationStatus(report.Status);
                StatusText = $"{report.Status}: {displayName}";
            }), cancellationToken).ConfigureAwait(true);

            row.ActiveOperation = null;
            row.Status = MapOperationStatus(finished.Status);
            row.LastOperation = finished;
            if (finished.Status == UpdateStatus.Succeeded)
            {
                row.AvailableUpdate = null;
            }
            RefreshUpdateCounts();
            outcomes.Add((row, finished));
            _logger.LogInformation(
                "Update run: {Device} finished with {Status} after {Duration}{Error}",
                displayName, finished.Status, finished.Duration ?? TimeSpan.Zero,
                string.IsNullOrWhiteSpace(finished.ErrorMessage) ? string.Empty : " - " + finished.ErrorMessage);

            await RecordIfProvenIneffectiveAsync(row, finished, cancellationToken).ConfigureAwait(true);

            if (finished.Candidate.InstallKind == UpdateInstallKind.VendorInstaller)
            {
                ApplySharedVendorInstallerResult(finished, row, originalUpdateId);
            }

            if (!dryRun
                && finished.Status == UpdateStatus.Skipped
                && finished.Candidate.InstallKind == UpdateInstallKind.VendorPage)
            {
                vendorPageFallbacks.Add(row);
            }
        }

        if (vendorPageFallbacks.Count > 0)
        {
            await OpenVendorPagesAsync(vendorPageFallbacks, cancellationToken).ConfigureAwait(true);
        }

        LogRunSummary(runStartedAt, dryRun, vendorPageFallbacks, installTargets, outcomes, skipped);

        StatusText = dryRun
            ? $"Dry run completed for {installTargets.Length} drivers."
            : vendorPageFallbacks.Count > 0
                ? $"Install completed for {installTargets.Length} drivers. Opened {vendorPageFallbacks.Count} vendor pages."
                : includeVendorPages
                    ? $"Install completed for {installTargets.Length} drivers."
                    : $"Install completed for {installTargets.Length} confirmed drivers.";

        if (!dryRun)
        {
            // Persist the post-install state so the next launch's cached view reflects what
            // was actually installed (succeeded rows now have AvailableUpdate cleared);
            // otherwise the cache would keep showing them as Outdated until the next scan.
            await SaveDriverCacheAsync(cancellationToken).ConfigureAwait(true);

            MaybePromptForRestart(outcomes);
        }
    }

    // When at least one driver update finished with "reboot required", ask the user (once, at
    // the very end of the run) whether to restart now, and restart if they accept. The cache
    // has already been saved above, so a restart here does not lose the post-install state.
    private void MaybePromptForRestart(
        IReadOnlyList<(DriverRowViewModel Row, UpdateOperation Operation)> outcomes)
    {
        if (_rebootPrompt is null)
        {
            return;
        }

        var rebootRequiredCount = outcomes.Count(o =>
            o.Operation.Status == UpdateStatus.Succeeded
            && o.Operation.ErrorMessage?.Contains("reboot", StringComparison.OrdinalIgnoreCase) == true);
        if (rebootRequiredCount == 0)
        {
            return;
        }

        _logger.LogInformation(
            "{Count} driver update(s) require a restart to finish; prompting the user.", rebootRequiredCount);

        if (_rebootPrompt.ConfirmRestartNow(rebootRequiredCount))
        {
            _logger.LogInformation("User accepted restart to complete {Count} driver update(s).", rebootRequiredCount);
            StatusText = "Restarting to finish driver installation...";
            _rebootPrompt.RestartNow();
        }
        else
        {
            _logger.LogInformation(
                "User deferred restart; {Count} update(s) will bind on the next reboot.", rebootRequiredCount);
            StatusText = $"Install completed. Restart later to finish {rebootRequiredCount} update(s).";
        }
    }

    private void LogRunSummary(
        DateTimeOffset runStartedAt,
        bool dryRun,
        IReadOnlyList<DriverRowViewModel> vendorPageFallbacks,
        IReadOnlyList<DriverRowViewModel> installTargets,
        IReadOnlyList<(DriverRowViewModel Row, UpdateOperation Operation)> outcomes,
        IReadOnlyList<(DriverRowViewModel Row, string Reason)> skipped)
    {
        var elapsed = DateTimeOffset.UtcNow - runStartedAt;
        var succeeded = outcomes.Where(o => o.Operation.Status == UpdateStatus.Succeeded).ToArray();
        var failed = outcomes.Where(o => o.Operation.Status == UpdateStatus.Failed).ToArray();
        var pipelineSkipped = outcomes.Where(o => o.Operation.Status is UpdateStatus.Skipped or UpdateStatus.Cancelled).ToArray();

        var sb = new System.Text.StringBuilder();
        sb.Append("Update run summary").Append(dryRun ? " (dry run)" : string.Empty)
            .Append(" - elapsed ").Append(elapsed.ToString(@"mm\:ss"))
            .Append(", install targets ").Append(installTargets.Count)
            .Append(", vendor pages opened ").Append(vendorPageFallbacks.Count)
            .Append(", succeeded ").Append(succeeded.Length)
            .Append(", failed ").Append(failed.Length)
            .Append(", skipped ").Append(pipelineSkipped.Length + skipped.Count)
            .AppendLine();

        if (succeeded.Length > 0)
        {
            sb.AppendLine("  Succeeded:");
            foreach (var (row, op) in succeeded)
            {
                var reboot = op.ErrorMessage?.Contains("reboot", StringComparison.OrdinalIgnoreCase) == true
                    ? " [REBOOT REQUIRED]" : string.Empty;
                sb.Append("    - ").Append(row.DeviceName)
                    .Append(" [").Append(row.HardwareId).Append(']')
                    .Append(": ").Append(op.TargetSnapshot.CurrentVersion?.ToString() ?? "?")
                    .Append(" → ").Append(op.Candidate.NewVersion?.ToString() ?? "?")
                    .Append(" via ").Append(op.Candidate.Source).Append('/').Append(op.Candidate.InstallKind)
                    .AppendLine(reboot);
            }
        }
        if (failed.Length > 0)
        {
            sb.AppendLine("  Failed:");
            foreach (var (row, op) in failed)
            {
                sb.Append("    - ").Append(row.DeviceName)
                    .Append(" [").Append(row.HardwareId).Append(']')
                    .Append(": ").Append(string.IsNullOrWhiteSpace(op.ErrorMessage) ? "(no error message)" : op.ErrorMessage)
                    .AppendLine();
            }
        }
        if (pipelineSkipped.Length > 0)
        {
            sb.AppendLine("  Skipped by pipeline:");
            foreach (var (row, op) in pipelineSkipped)
            {
                sb.Append("    - ").Append(row.DeviceName)
                    .Append(": ").Append(string.IsNullOrWhiteSpace(op.ErrorMessage) ? op.Status.ToString() : op.ErrorMessage)
                    .AppendLine();
            }
        }
        if (skipped.Count > 0)
        {
            sb.AppendLine("  Skipped before pipeline:");
            foreach (var (row, reason) in skipped)
            {
                sb.Append("    - ").Append(row.DeviceName).Append(": ").AppendLine(reason);
            }
        }

        _logger.LogInformation("{Summary}", sb.ToString().TrimEnd());
    }

    private void LogScanSummary()
    {
        var withUpdates = Drivers.Where(d => d.AvailableUpdate != null).ToArray();
        var upToDateCount = Drivers.Count - withUpdates.Length;

        var sb = new System.Text.StringBuilder();
        sb.Append("Scan result summary: ").Append(Drivers.Count).Append(" total drivers, ")
            .Append(withUpdates.Length).Append(" with available updates, ")
            .Append(upToDateCount).AppendLine(" up-to-date / no update found");

        if (withUpdates.Length > 0)
        {
            sb.AppendLine("  Updates found:");
            foreach (var row in withUpdates)
            {
                sb.Append("    - ").Append(row.DeviceName)
                    .Append(" [").Append(row.HardwareId).Append(']')
                    .Append(": installed=").Append(
                        row.Driver.CurrentVersion?.ToString()
                        ?? row.Driver.CurrentDate?.ToString()
                        ?? "?")
                    .Append(", available=").Append(row.AvailableUpdate!.NewVersion?.ToString() ?? "?")
                    .Append(", source=").Append(row.AvailableUpdate.Source)
                    .AppendLine();
            }
        }

        _logger.LogInformation("{Summary}", sb.ToString().TrimEnd());
    }

    private void ApplySharedVendorInstallerResult(UpdateOperation finished, DriverRowViewModel masterRow, string originalUpdateId)
    {
        // Every row that shares the SourceUpdateId is really the same install (think 18
        // AMD chipset device rows that all point at amd_chipset_software_X.Y.Z.exe). Once
        // the master row finishes, those duplicate rows have already been touched in the
        // same way and should disappear from the grid: keeping them in the Installable
        // filter makes it look like there is still work pending when there is not.
        // The master row keeps its AvailableUpdate on failure so the user can retry it
        // explicitly without having to rescan.
        // A vendor page candidate that was resolved to a direct installer finishes with a
        // rewritten SourceUpdateId; sibling rows still carry the original id, so match both.
        foreach (var row in Drivers.Where(r =>
            r.AvailableUpdate?.SourceUpdateId is { } id
            && (string.Equals(id, finished.Candidate.SourceUpdateId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(id, originalUpdateId, StringComparison.OrdinalIgnoreCase))))
        {
            row.Status = MapOperationStatus(finished.Status);
            row.LastOperation = finished;
            if (finished.Status == UpdateStatus.Succeeded || !ReferenceEquals(row, masterRow))
            {
                row.AvailableUpdate = null;
            }
        }

        RefreshUpdateCounts();
    }

    private async Task OpenVendorPagesAsync(IEnumerable<DriverRowViewModel> targets, CancellationToken cancellationToken)
    {
        var opener = _updatePageOpener;
        if (opener is null)
        {
            return;
        }

        var candidates = targets
            .Select(t => t.AvailableUpdate)
            .OfType<UpdateCandidate>()
            .DistinctBy(c => c.DownloadUrl.AbsoluteUri, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        for (var i = 0; i < candidates.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                opener.Open(candidates[i]);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to open vendor update page {Url}", candidates[i].DownloadUrl);
            }

            // Stagger tab openings so the browser doesn't get flooded all at once.
            if (i < candidates.Length - 1)
            {
                await Task.Delay(150, cancellationToken).ConfigureAwait(true);
            }
        }
    }

    private static DriverStatus MapOperationStatus(UpdateStatus status) => status switch
    {
        UpdateStatus.Succeeded => DriverStatus.UpToDate,
        UpdateStatus.Failed => DriverStatus.Error,
        UpdateStatus.RolledBack => DriverStatus.Outdated,
        UpdateStatus.Cancelled or UpdateStatus.Skipped => DriverStatus.Outdated,
        _ => DriverStatus.Outdated
    };

    [RelayCommand(CanExecute = nameof(CanOpenOemTool))]
    private void OpenOemTool()
    {
        var oem = DetectedOem;
        if (oem is null)
        {
            return;
        }

        try
        {
            if (oem.ToolInstalled && !string.IsNullOrEmpty(oem.ToolPath))
            {
                var psi = new ProcessStartInfo
                {
                    FileName = oem.ToolPath,
                    UseShellExecute = true
                };
                Process.Start(psi);
                _logger.LogInformation("Launched OEM tool {Tool}", oem.ToolName);
            }
            else
            {
                var psi = new ProcessStartInfo
                {
                    FileName = oem.FallbackUrl.AbsoluteUri,
                    UseShellExecute = true
                };
                Process.Start(psi);
                _logger.LogInformation("Opened OEM URL {Url}", oem.FallbackUrl);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not open OEM tool or URL");
            StatusText = $"Could not open {oem.ToolName}: {ex.Message}";
        }
    }

    private bool CanOpenOemTool() => DetectedOem is not null;

    private bool FilterDriver(object? item)
    {
        if (item is not DriverRowViewModel row)
        {
            return false;
        }

        if (CategoryFilter is { } category && row.Category != category)
        {
            return false;
        }

        if (!MatchesUpdateFilter(row))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var needle = SearchText.Trim();
            return Contains(row.DeviceName, needle)
                || Contains(row.Provider, needle)
                || Contains(row.Manufacturer, needle)
                || Contains(row.HardwareId, needle);
        }

        return true;
    }

    private static string DriverDisplayName(DriverRowViewModel row) =>
        string.IsNullOrWhiteSpace(row.DeviceName) ? $"[{row.HardwareId}]" : row.DeviceName;

    private static bool Contains(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private bool MatchesUpdateFilter(DriverRowViewModel row) => UpdateFilter switch
    {
        DriverUpdateFilter.All => true,
        DriverUpdateFilter.ConfirmedUpdates => row.AvailableUpdate?.Confidence == UpdateConfidence.Confirmed,
        DriverUpdateFilter.VendorChecks => row.AvailableUpdate?.Confidence == UpdateConfidence.Advisory,
        DriverUpdateFilter.Installable => row.AvailableUpdate?.InstallKind is UpdateInstallKind.WindowsUpdate or UpdateInstallKind.PnPUtilPackage or UpdateInstallKind.VendorInstaller or UpdateInstallKind.VendorPage,
        DriverUpdateFilter.NoUpdate => row.AvailableUpdate is null,
        _ => true
    };
}
