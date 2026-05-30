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
using Microsoft.Extensions.Logging;

namespace DriverUpdater.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IDriverScanService _scanService;
    private readonly IReadOnlyList<IUpdateSource> _updateSources;
    private readonly IOemDetectionService _oemDetectionService;
    private readonly IInstallPipeline _installPipeline;
    private readonly IInstallConfirmation _installConfirmation;
    private readonly IUpdatePageOpener? _updatePageOpener;
    private readonly IHistoryWindowOpener _historyWindowOpener;
    private readonly ISettingsWindowOpener _settingsWindowOpener;
    private readonly ILogsWindowOpener _logsWindowOpener;
    private readonly IDriverCacheStore? _driverCacheStore;
    private readonly IAiVerifier? _aiVerifier;
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
    private bool _isScanning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    private int _scannedCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    [NotifyCanExecuteChangedFor(nameof(UpdateOutdatedCommand))]
    [NotifyCanExecuteChangedFor(nameof(UpdateAllCommand))]
    [NotifyCanExecuteChangedFor(nameof(DryRunOutdatedCommand))]
    private int _updatesFoundCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    [NotifyCanExecuteChangedFor(nameof(InstallConfirmedCommand))]
    private int _confirmedUpdatesCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    [NotifyCanExecuteChangedFor(nameof(OpenVendorChecksCommand))]
    private int _vendorChecksCount;

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
        IAiVerifier? aiVerifier = null)
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
        _driverCacheStore = driverCacheStore;
        _aiVerifier = aiVerifier;
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
    }

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

            foreach (var entry in snapshot.Entries)
            {
                var row = new DriverRowViewModel(entry.Driver)
                {
                    Status = entry.Status,
                    AvailableUpdate = entry.AvailableUpdate
                };
                Drivers.Add(row);
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
        if (_updateSources.Count == 0 || Drivers.Count == 0)
        {
            return;
        }

        var index = BuildHardwareIdIndex();
        var driverSnapshots = Drivers.Select(d => d.Driver).ToArray();

        foreach (var source in _updateSources)
        {
            try
            {
                StatusText = $"Querying {source.DisplayName}...";
                _logger.LogInformation("Querying {Source}", source.DisplayName);

                await foreach (var candidate in source.SearchAsync(driverSnapshots, cancellationToken))
                {
                    if (TryFindRow(index, candidate.ForHardwareId, out var row, out var matchKind)
                        && candidate.IsNewerThan(row.Driver)
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
    }

    // Best-effort post-scan pass. When an AI provider is configured it reviews every
    // installable candidate in one batched call to (1) suppress updates that are not
    // genuinely newer than what is installed and (2) annotate the rest with a risk
    // assessment. Any failure leaves the scan results exactly as they were.
    private async Task VerifyCandidatesWithAiAsync(CancellationToken cancellationToken)
    {
        if (_aiVerifier is null || !_aiVerifier.IsConfigured)
        {
            return;
        }

        var targets = Drivers
            .Where(r => r.AvailableUpdate is { InstallKind: not UpdateInstallKind.VendorPage })
            .ToArray();
        if (targets.Length == 0)
        {
            return;
        }

        var requests = targets
            .Select(r => new AiVerificationRequest(
                CorrelationId: r.AvailableUpdate!.SourceUpdateId,
                DeviceName: r.DeviceName,
                HardwareId: r.HardwareId,
                InstalledVersion: r.Driver.CurrentVersion?.ToString(),
                InstalledDate: r.Driver.CurrentDate,
                CandidateVersion: r.AvailableUpdate.NewVersion.ToString(),
                CandidateDate: r.AvailableUpdate.NewDate,
                Source: r.AvailableUpdate.Source,
                DownloadUrl: r.AvailableUpdate.DownloadUrl.AbsoluteUri))
            .ToArray();

        IReadOnlyDictionary<string, AiVerdict> verdicts;
        try
        {
            StatusText = "Verifying updates with AI...";
            _logger.LogInformation("Requesting AI verification for {Count} candidates", requests.Length);
            verdicts = await _aiVerifier.VerifyAsync(requests, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AI verification failed; leaving scan results unchanged");
            return;
        }

        if (verdicts.Count == 0)
        {
            return;
        }

        var suppressed = 0;
        var annotated = 0;
        foreach (var row in targets)
        {
            var candidate = row.AvailableUpdate;
            if (candidate is null || !verdicts.TryGetValue(candidate.SourceUpdateId, out var verdict))
            {
                continue;
            }

            if (!verdict.IsGenuinelyNewer)
            {
                _logger.LogInformation(
                    "AI suppressed {Device}: {Summary}", row.DeviceName, verdict.Summary);
                row.AvailableUpdate = null;
                row.Status = DriverStatus.UpToDate;
                suppressed++;
                continue;
            }

            row.AvailableUpdate = candidate with { AiVerification = verdict };
            annotated++;
        }

        RefreshUpdateCounts();
        _logger.LogInformation(
            "AI verification applied: {Suppressed} suppressed, {Annotated} annotated", suppressed, annotated);
        StatusText = $"AI verification complete. {suppressed} suppressed, {annotated} annotated.";
    }

    private Dictionary<string, List<DriverRowViewModel>> BuildHardwareIdIndex()
    {
        var dict = new Dictionary<string, List<DriverRowViewModel>>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Drivers)
        {
            var key = row.HardwareId;
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }
            if (!dict.TryGetValue(key, out var bucket))
            {
                bucket = new List<DriverRowViewModel>();
                dict[key] = bucket;
            }
            bucket.Add(row);
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

    // True when one of the IDs is a clean prefix of the other, where "clean" means the
    // next character after the prefix is a Windows hardware-ID separator (\ or &). Without
    // that boundary, IDs like ROOT\X and ROOT\XYZ would match each other coincidentally,
    // which has caused cross-vendor confusion (an AMD chipset candidate landing on a
    // Logitech row, for example).
    internal static bool IsBoundaryPrefix(string a, string b)
    {
        if (a.Length == b.Length)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
        var (shorter, longer) = a.Length < b.Length ? (a, b) : (b, a);
        if (!longer.StartsWith(shorter, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var nextChar = longer[shorter.Length];
        return nextChar == '\\' || nextChar == '&';
    }

    private void RefreshUpdateCounts()
    {
        UpdatesFoundCount = Drivers.Count(d => d.Status == DriverStatus.Outdated);
        ConfirmedUpdatesCount = Drivers.Count(d => d.AvailableUpdate?.Confidence == UpdateConfidence.Confirmed);
        VendorChecksCount = Drivers.Count(d => d.AvailableUpdate?.Confidence == UpdateConfidence.Advisory);
    }

    private static bool ShouldAcceptCandidate(DriverRowViewModel row, UpdateCandidate candidate)
    {
        var current = row.AvailableUpdate;
        if (current is null)
        {
            return true;
        }

        var currentPriority = CandidatePriority(current);
        var newPriority = CandidatePriority(candidate);
        if (newPriority < currentPriority)
        {
            return false;
        }
        if (newPriority > currentPriority)
        {
            return true;
        }

        var versionComparison = candidate.NewVersion.CompareTo(current.NewVersion);
        if (versionComparison != 0)
        {
            return versionComparison > 0;
        }

        return candidate.NewDate > current.NewDate;
    }

    private static int CandidatePriority(UpdateCandidate candidate) =>
        candidate.Confidence == UpdateConfidence.Confirmed ? 2 : 1;

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
        await RunUpdatesAsync(Drivers, dryRun: false, includeVendorPages: false, cancellationToken).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanRunAnyUpdates))]
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
    private void OpenVendorChecks()
    {
        var pageTargets = Drivers
            .Where(r => r.Status == DriverStatus.Outdated
                && r.AvailableUpdate is { InstallKind: UpdateInstallKind.VendorPage })
            .ToArray();

        if (pageTargets.Length == 0)
        {
            StatusText = "No vendor checks to open.";
            return;
        }

        OpenVendorPages(pageTargets);
        StatusText = $"Opened {pageTargets.Length} vendor update pages.";
    }

    private bool CanRunAnyUpdates() => UpdatesFoundCount > 0;

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
            .Where(r => r.Status == DriverStatus.Outdated && r.AvailableUpdate is not null)
            .ToArray();

        if (targets.Length == 0)
        {
            StatusText = "No outdated drivers to update.";
            return;
        }

        var installTargets = targets
            .Where(r => r.AvailableUpdate is { InstallKind: UpdateInstallKind.WindowsUpdate or UpdateInstallKind.PnPUtilPackage or UpdateInstallKind.VendorInstaller })
            .ToArray();
        var pageTargets = targets
            .Where(r => r.AvailableUpdate is { InstallKind: UpdateInstallKind.VendorPage })
            .ToArray();

        if (!dryRun && includeVendorPages && pageTargets.Length > 0)
        {
            OpenVendorPages(pageTargets);
        }

        if (installTargets.Length == 0)
        {
            StatusText = dryRun
                ? $"Dry run completed. {pageTargets.Length} vendor pages would be opened."
                : includeVendorPages
                    ? $"Opened {pageTargets.Length} vendor update pages."
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
        foreach (var row in installTargets)
        {
            if (row.AvailableUpdate is null)
            {
                _logger.LogInformation(
                    "Update run: skipping {Device} - candidate was already cleared by an earlier shared install",
                    row.DeviceName);
                skipped.Add((row, "candidate was already cleared by an earlier shared install"));
                continue;
            }
            if (!processedUpdateIds.Add(row.AvailableUpdate.SourceUpdateId))
            {
                _logger.LogInformation(
                    "Update run: skipping {Device} - deduplicated, same installer as a previous row ({SourceUpdateId})",
                    row.DeviceName, row.AvailableUpdate.SourceUpdateId);
                skipped.Add((row, $"deduplicated - same installer as a previous row ({row.AvailableUpdate.SourceUpdateId})"));
                continue;
            }
            cancellationToken.ThrowIfCancellationRequested();

            var op = UpdateOperation.NewPending(row.AvailableUpdate, row.Driver);
            row.ActiveOperation = op;
            StatusText = (dryRun ? "Dry run: " : "Installing: ") + row.DeviceName;
            ScrollToRowRequested?.Invoke(this, row);
            _logger.LogInformation(
                "Update run: starting {Device} (current version={CurrentVersion}, target version={TargetVersion}, source={Source}, kind={Kind}, url={Url})",
                row.DeviceName, row.Driver.CurrentVersion, row.AvailableUpdate.NewVersion,
                row.AvailableUpdate.Source, row.AvailableUpdate.InstallKind, row.AvailableUpdate.DownloadUrl);

            var finished = await _installPipeline.ExecuteAsync(op, options, new Progress<UpdateOperation>(report =>
            {
                row.ActiveOperation = report;
                row.Status = MapOperationStatus(report.Status);
                StatusText = $"{report.Status}: {row.DeviceName}";
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
                row.DeviceName, finished.Status, finished.Duration ?? TimeSpan.Zero,
                string.IsNullOrWhiteSpace(finished.ErrorMessage) ? string.Empty : " - " + finished.ErrorMessage);

            if (finished.Candidate.InstallKind == UpdateInstallKind.VendorInstaller)
            {
                ApplySharedVendorInstallerResult(finished, row);
            }
        }

        LogRunSummary(runStartedAt, dryRun, pageTargets, installTargets, outcomes, skipped);

        StatusText = dryRun
            ? $"Dry run completed for {installTargets.Length} drivers."
            : includeVendorPages
                ? $"Install completed for {installTargets.Length} drivers. Opened {pageTargets.Length} vendor pages."
                : $"Install completed for {installTargets.Length} confirmed drivers.";

        if (!dryRun)
        {
            // Persist the post-install state so the next launch's cached view reflects what
            // was actually installed (succeeded rows now have AvailableUpdate cleared);
            // otherwise the cache would keep showing them as Outdated until the next scan.
            await SaveDriverCacheAsync(cancellationToken).ConfigureAwait(true);
        }
    }

    private void LogRunSummary(
        DateTimeOffset runStartedAt,
        bool dryRun,
        IReadOnlyList<DriverRowViewModel> pageTargets,
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
            .Append(", vendor pages ").Append(pageTargets.Count)
            .Append(", succeeded ").Append(succeeded.Length)
            .Append(", failed ").Append(failed.Length)
            .Append(", skipped ").Append(pipelineSkipped.Length + skipped.Count)
            .AppendLine();

        if (succeeded.Length > 0)
        {
            sb.AppendLine("  Succeeded:");
            foreach (var (row, op) in succeeded)
            {
                sb.Append("    - ").Append(row.DeviceName).Append(" -> ").Append(op.Candidate.NewVersion).AppendLine();
            }
        }
        if (failed.Length > 0)
        {
            sb.AppendLine("  Failed:");
            foreach (var (row, op) in failed)
            {
                sb.Append("    - ").Append(row.DeviceName)
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

    private void ApplySharedVendorInstallerResult(UpdateOperation finished, DriverRowViewModel masterRow)
    {
        // Every row that shares the SourceUpdateId is really the same install (think 18
        // AMD chipset device rows that all point at amd_chipset_software_X.Y.Z.exe). Once
        // the master row finishes, those duplicate rows have already been touched in the
        // same way and should disappear from the grid: keeping them in the Installable
        // filter makes it look like there is still work pending when there is not.
        // The master row keeps its AvailableUpdate on failure so the user can retry it
        // explicitly without having to rescan.
        foreach (var row in Drivers.Where(r => r.AvailableUpdate?.SourceUpdateId == finished.Candidate.SourceUpdateId))
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

    private void OpenVendorPages(IEnumerable<DriverRowViewModel> targets)
    {
        var opener = _updatePageOpener;
        if (opener is null)
        {
            return;
        }

        foreach (var candidate in targets
            .Select(t => t.AvailableUpdate)
            .OfType<UpdateCandidate>()
            .DistinctBy(c => c.DownloadUrl.AbsoluteUri, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                opener.Open(candidate);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to open vendor update page {Url}", candidate.DownloadUrl);
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

    private static bool Contains(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private bool MatchesUpdateFilter(DriverRowViewModel row) => UpdateFilter switch
    {
        DriverUpdateFilter.All => true,
        DriverUpdateFilter.ConfirmedUpdates => row.AvailableUpdate?.Confidence == UpdateConfidence.Confirmed,
        DriverUpdateFilter.VendorChecks => row.AvailableUpdate?.Confidence == UpdateConfidence.Advisory,
        DriverUpdateFilter.Installable => row.AvailableUpdate?.InstallKind is UpdateInstallKind.WindowsUpdate or UpdateInstallKind.PnPUtilPackage or UpdateInstallKind.VendorInstaller,
        DriverUpdateFilter.NoUpdate => row.AvailableUpdate is null,
        _ => true
    };
}
