using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DriverUpdater.App.Ai;
using DriverUpdater.App.Logging;
using DriverUpdater.App.Services;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using DriverUpdater.Core.Options;
using DriverUpdater.Services.Scanning;
using DriverUpdater.Services.Sources;
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
    private readonly IAiTextCompleter? _driverChatCompleter;
    private readonly IPostUpdateSummaryCoordinator? _postUpdateSummaryCoordinator;
    private readonly ISupportWindowOpener? _supportWindowOpener;

    // (DeviceId|TargetVersion) -> installed version when the update was last proven ineffective.
    // Used for exact-target suppression from precise sources (vendor/OEM/AI).
    private Dictionary<string, string?> _ineffectiveIndex = new(StringComparer.OrdinalIgnoreCase);

    // DeviceId -> the set of installed versions that had a proven no-op. The Microsoft Update
    // Catalog re-versions the same generic/mismatched driver every scan (e.g. Computer Device
    // 30.100.2534.35 then .18), so exact-target matching alone lets each new build slip through.
    // For catalog/Windows-Update candidates we suppress at the device level: while the device
    // still reports an installed version that a catalog driver already failed to replace, skip
    // any catalog candidate for it. Reboot-required installs are never recorded, so legitimate
    // pending updates (Intel PMT, Iris Xe) are unaffected.
    private Dictionary<string, HashSet<string?>> _ineffectiveDeviceInstalled = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<MainViewModel> _logger;

    public ObservableCollection<DriverRowViewModel> Drivers { get; } = new();

    public ICollectionView DriversView { get; }

    public IReadOnlyList<DriverCategory> AvailableCategories { get; } =
        Enum.GetValues<DriverCategory>().ToArray();

    public IReadOnlyList<DriverUpdateFilterOption> AvailableUpdateFilters { get; } =
    [
        new(DriverUpdateFilter.AllDrivers, "All drivers"),
        new(DriverUpdateFilter.UpdatesAvailable, "Updates available"),
        new(DriverUpdateFilter.NoUpdateAvailable, "No update available")
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
    [NotifyCanExecuteChangedFor(nameof(UpdateAllCommand))]
    private int _updatesFoundCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    [NotifyCanExecuteChangedFor(nameof(UpdateAllCommand))]
    private int _confirmedUpdatesCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    [NotifyCanExecuteChangedFor(nameof(OpenVendorChecksCommand))]
    [NotifyCanExecuteChangedFor(nameof(UpdateAllCommand))]
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
    private DriverUpdateFilter _updateFilter = DriverUpdateFilter.AllDrivers;

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
        IIneffectiveUpdateStore? ineffectiveUpdateStore = null,
        IAiTextCompleter? driverChatCompleter = null,
        IPostUpdateSummaryCoordinator? postUpdateSummaryCoordinator = null,
        ISupportWindowOpener? supportWindowOpener = null)
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
        _driverChatCompleter = driverChatCompleter;
        _postUpdateSummaryCoordinator = postUpdateSummaryCoordinator;
        _supportWindowOpener = supportWindowOpener;
        _logger = logger;

        DriversView = CollectionViewSource.GetDefaultView(Drivers);
        DriversView.Filter = FilterDriver;

        DriverChatMessages.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasDriverChat));
            OnPropertyChanged(nameof(HasNoDriverChat));
        };
    }

    // ----- AI chat about the scanned drivers -----

    public ObservableCollection<LogChatMessage> DriverChatMessages { get; } = new();

    /// <summary>Toggles the driver AI chat panel (the sparkle button). Closed by default.</summary>
    [ObservableProperty]
    private bool _isDriverChatVisible;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendDriverChatCommand))]
    private bool _isDriverChatting;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendDriverChatCommand))]
    private string _driverChatInput = string.Empty;

    public bool HasDriverChat => DriverChatMessages.Count > 0;

    public bool HasNoDriverChat => DriverChatMessages.Count == 0;

    [RelayCommand]
    private async Task InstallAiRecommendedAsync(LogChatMessage? message, CancellationToken cancellationToken)
    {
        if (message?.RecommendedHardwareIds is not { Count: > 0 } ids)
        {
            return;
        }

        var rows = MatchRecommendedRows(ids);
        if (rows.Length == 0)
        {
            StatusText = "The AI-recommended updates are no longer available. Rescan and ask again.";
            return;
        }

        await RunUpdatesAsync(rows, dryRun: false, includeVendorPages: true, cancellationToken).ConfigureAwait(true);
    }

    private DriverRowViewModel[] MatchRecommendedRows(IReadOnlyList<string> hardwareIds) =>
        Drivers
            .Where(r => r.AvailableUpdate is not null
                && hardwareIds.Contains(r.HardwareId, StringComparer.OrdinalIgnoreCase))
            .ToArray();

    private bool CanSendDriverChat() => !IsDriverChatting && !string.IsNullOrWhiteSpace(DriverChatInput);

    [RelayCommand(CanExecute = nameof(CanSendDriverChat), IncludeCancelCommand = true)]
    private async Task SendDriverChatAsync(CancellationToken cancellationToken)
    {
        var question = DriverChatInput?.Trim();
        if (string.IsNullOrWhiteSpace(question))
        {
            return;
        }
        if (_driverChatCompleter is null || !_driverChatCompleter.IsConfigured)
        {
            DriverChatMessages.Add(new LogChatMessage(IsUser: false,
                "AI is not configured. Open Settings > AI to enable it, then ask again."));
            DriverChatInput = string.Empty;
            return;
        }

        var context = BuildDriverChatContext();
        var history = DriverChatMessages.Where(m => !string.IsNullOrWhiteSpace(m.Text)).ToArray();
        DriverChatMessages.Add(new LogChatMessage(IsUser: true, question));
        DriverChatInput = string.Empty;
        IsDriverChatting = true;
        StatusText = "Asking AI about your drivers...";
        try
        {
            var prompt = DriverChatPromptBuilder.Build(context, history, question);
            var answer = await _driverChatCompleter.CompleteAsync(prompt, cancellationToken).ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(answer))
            {
                DriverChatMessages.Add(new LogChatMessage(IsUser: false,
                    "(No response from AI. Check the AI provider in Settings and try again.)"));
                StatusText = "AI did not return an answer.";
                return;
            }

            var (text, recommendedIds) = DriverChatActionParser.Parse(answer);
            var matched = MatchRecommendedRows(recommendedIds);
            if (!string.IsNullOrWhiteSpace(text))
            {
                DriverChatMessages.Add(new LogChatMessage(IsUser: false, text));
            }
            else if (matched.Length == 0)
            {
                DriverChatMessages.Add(new LogChatMessage(IsUser: false, answer.Trim()));
            }

            if (matched.Length > 0)
            {
                DriverChatMessages.Add(new LogChatMessage(IsUser: false, string.Empty,
                    matched.Select(r => r.HardwareId).ToArray()));
                StatusText = $"AI recommends installing {matched.Length} update(s). Press the button in the chat.";
            }
            else
            {
                if (recommendedIds.Count > 0)
                {
                    _logger.LogInformation(
                        "Driver chat: AI recommended {Count} hardware IDs but none matched a row with an available update",
                        recommendedIds.Count);
                }
                StatusText = "AI answered. Ask a follow-up or clear the chat.";
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "AI chat cancelled.";
        }
        catch (Exception ex)
        {
            DriverChatMessages.Add(new LogChatMessage(IsUser: false, $"(AI chat failed: {ex.Message})"));
            StatusText = $"AI chat failed: {ex.Message}";
        }
        finally
        {
            IsDriverChatting = false;
        }
    }

    [RelayCommand]
    private void ClearDriverChat()
    {
        DriverChatMessages.Clear();
        StatusText = "Driver chat cleared.";
    }

    private IReadOnlyList<DriverChatContextItem> BuildDriverChatContext() =>
        Drivers.Select(r => new DriverChatContextItem(
            DeviceName: r.DeviceName,
            HardwareId: r.HardwareId,
            Category: r.Category.ToString(),
            CurrentVersion: r.Driver.CurrentVersion?.ToString() ?? r.Driver.CurrentDate?.ToString(),
            Status: r.Status.ToString(),
            AvailableVersion: r.AvailableUpdate?.NewVersion?.ToString(),
            AvailableSource: r.AvailableUpdate?.Source.ToString())).ToList();

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

        if (_postUpdateSummaryCoordinator is not null)
        {
            await _postUpdateSummaryCoordinator.ResumeAfterRestartAsync(cancellationToken).ConfigureAwait(true);
        }

        await CheckForAppUpdateAsync(cancellationToken).ConfigureAwait(true);
    }

    private async Task CheckForAppUpdateAsync(CancellationToken cancellationToken)
    {
        if (_appUpdater is null)
        {
            return;
        }

        // Off by default: only check for and offer an app update on launch when the user has
        // opted in via Settings > "Check for updates on startup". Manual checks in Settings
        // work regardless of this flag.
        if (_updaterSettings?.CurrentValue.CheckOnStartup != true)
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

            if (_updaterSettings?.CurrentValue.AutoApply == true)
            {
                await UpdateAppAsync(cancellationToken).ConfigureAwait(true);
                return;
            }

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
        var previousRows = SnapshotRowsByDeviceId();
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
            MergePreviousRows(previousRows);
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

    private Dictionary<string, DriverRowViewModel> SnapshotRowsByDeviceId()
    {
        var map = new Dictionary<string, DriverRowViewModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Drivers)
        {
            if (!string.IsNullOrWhiteSpace(row.Driver.DeviceId))
            {
                map[row.Driver.DeviceId] = row;
            }
        }
        return map;
    }

    // The grid (and the cache built from it) is accumulating: drivers seen in any
    // earlier scan stay in the list even when the current WMI scan does not report
    // them, and a pending update found earlier is kept until the installed driver
    // catches up or a source offers something newer (DriverUpdateMatcher decides).
    private void MergePreviousRows(IReadOnlyDictionary<string, DriverRowViewModel> previousRows)
    {
        if (previousRows.Count == 0)
        {
            return;
        }

        var restoredUpdates = 0;
        var scannedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Drivers)
        {
            scannedIds.Add(row.Driver.DeviceId);
            if (row.AvailableUpdate is not null
                || !previousRows.TryGetValue(row.Driver.DeviceId, out var previous)
                || previous.AvailableUpdate is not { } pending)
            {
                continue;
            }

            if (pending.IsNewerThan(row.Driver))
            {
                row.AvailableUpdate = pending;
                row.Status = previous.Status;
                restoredUpdates++;
            }
            else
            {
                _logger.LogInformation(
                    "Cache merge: dropped pending update for {Device} - installed driver (version={Version}, date={Date}) caught up with cached candidate {Candidate}",
                    row.DeviceName, row.Driver.CurrentVersion, row.Driver.CurrentDate, pending.NewVersion);
            }
        }

        var keptRows = 0;
        foreach (var (deviceId, previous) in previousRows)
        {
            if (scannedIds.Contains(deviceId))
            {
                continue;
            }
            Drivers.Add(new DriverRowViewModel(previous.Driver)
            {
                Status = previous.Status,
                AvailableUpdate = previous.AvailableUpdate
            });
            keptRows++;
            _logger.LogDebug(
                "Cache merge: keeping {Device} ({DeviceId}) - present in an earlier scan but missing from this one",
                previous.DeviceName, deviceId);
        }

        ScannedCount = Drivers.Count;
        RefreshUpdateCounts();
        _logger.LogInformation(
            "Cache merge: {Kept} driver(s) kept from earlier scans, {Restored} pending update(s) restored (grid now {Total} rows)",
            keptRows, restoredUpdates, Drivers.Count);
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

        var rowsReviewedAsCandidates = await VerifyCandidatesWithAiAsync(cancellationToken).ConfigureAwait(true);
        await DiscoverLatestDriversWithAiAsync(
            onlyRowsWithoutUpdates: true,
            rowsReviewedAsCandidates,
            cancellationToken).ConfigureAwait(true);

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

            _ineffectiveDeviceInstalled = new Dictionary<string, HashSet<string?>>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in records)
            {
                if (!_ineffectiveDeviceInstalled.TryGetValue(r.DeviceId, out var installedVersions))
                {
                    installedVersions = new HashSet<string?>(StringComparer.OrdinalIgnoreCase);
                    _ineffectiveDeviceInstalled[r.DeviceId] = installedVersions;
                }
                installedVersions.Add(r.InstalledVersionAtAttempt);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load the ineffective-update ledger; not suppressing any candidates");
            _ineffectiveIndex = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            _ineffectiveDeviceInstalled = new Dictionary<string, HashSet<string?>>(StringComparer.OrdinalIgnoreCase);
        }
    }

    // A candidate is a proven no-op when we previously installed this exact target for this device
    // and Windows kept the existing driver (no reboot pending), AND the device still reports the
    // same installed version - so re-installing would change nothing again. If the installed
    // version has since changed, the record no longer applies and the candidate is offered again.
    private bool IsProvenIneffective(DriverRowViewModel row, UpdateCandidate candidate)
    {
        if (candidate.NewVersion is null)
        {
            return false;
        }

        var deviceId = row.Driver.DeviceId;
        var currentInstalled = row.Driver.CurrentVersion?.ToString();

        // Device-level suppression for the Microsoft Update Catalog / Windows Update, which
        // re-version the same generic driver every scan. If a catalog driver already failed to
        // replace this device's currently-installed driver, skip any catalog candidate for it
        // (regardless of the exact build number) until the installed driver actually changes.
        if (candidate.Source is UpdateSource.MicrosoftCatalog or UpdateSource.WindowsUpdate
            && _ineffectiveDeviceInstalled.TryGetValue(deviceId, out var installedVersions)
            && installedVersions.Contains(currentInstalled))
        {
            _logger.LogInformation(
                "Suppressing {Device}: a {Source} driver already failed to replace the installed {Installed} " +
                "(proven no-op); skipping {Target}. It will be offered again if the installed driver changes.",
                DriverDisplayName(row), candidate.Source, currentInstalled ?? "existing driver", candidate.NewVersion);
            return true;
        }

        // Exact-target suppression for precise sources (vendor/OEM/AI): only skip the same target.
        if (_ineffectiveIndex.TryGetValue(IneffectiveKey(deviceId, candidate.NewVersion.ToString()), out var installedAtAttempt)
            && string.Equals(installedAtAttempt, currentInstalled, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation(
                "Suppressing {Device}: {Target} was already installed but Windows kept {Installed} (proven no-op). " +
                "It will be offered again if the installed driver changes or a newer version appears.",
                DriverDisplayName(row), candidate.NewVersion, currentInstalled ?? "the existing driver");
            return true;
        }

        return false;
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
    private async Task<IReadOnlySet<DriverRowViewModel>> VerifyCandidatesWithAiAsync(
        CancellationToken cancellationToken)
    {
        if (_aiVerifier is null)
        {
            _logger.LogDebug("AI verification skipped: no verifier is registered");
            return new HashSet<DriverRowViewModel>();
        }
        if (!_aiVerifier.IsConfigured)
        {
            _logger.LogInformation(
                "AI verification skipped: provider {Provider} is not configured", _aiVerifier.Provider);
            return new HashSet<DriverRowViewModel>();
        }

        var targets = Drivers
            .Where(r => r.AvailableUpdate is not null)
            .ToArray();
        if (targets.Length == 0)
        {
            _logger.LogInformation("AI verification skipped: no existing candidates to verify");
            return new HashSet<DriverRowViewModel>();
        }
        var reviewedRows = targets.ToHashSet();

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
            StatusText = $"Verifying existing updates with AI... 1-{targets.Length} of {Drivers.Count}";
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
            return reviewedRows;
        }

        if (verdicts.Count == 0)
        {
            _logger.LogWarning(
                "AI verification returned no verdicts after {ElapsedMs} ms; leaving all {Count} candidate(s) unchanged",
                stopwatch.ElapsedMilliseconds, requests.Length);
            StatusText = "AI verification returned no usable result; scan results unchanged.";
            return reviewedRows;
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
        return reviewedRows;
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

    private async Task DiscoverLatestDriversWithAiAsync(
        bool onlyRowsWithoutUpdates,
        IReadOnlySet<DriverRowViewModel> alreadyReviewed,
        CancellationToken cancellationToken)
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
            .Where(r => !alreadyReviewed.Contains(r))
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
                var overallStart = alreadyReviewed.Count + processed + 1;
                var overallEnd = alreadyReviewed.Count + processed + batch.Length;
                StatusText =
                    $"Asking AI to find latest drivers... {overallStart}-{overallEnd} of {Drivers.Count}. Waiting for AI response...";
                _logger.LogInformation(
                    "AI latest-driver discovery: provider={Provider}, sending discovery batch {DiscoveryStart}-{DiscoveryEnd} of {DiscoveryTotal}, overall rows {OverallStart}-{OverallEnd} of {OverallTotal}",
                    _aiVerifier.Provider,
                    processed + 1,
                    processed + batch.Length,
                    targets.Length,
                    overallStart,
                    overallEnd,
                    Drivers.Count);
                foreach (var request in requests)
                {
                    LogAiRequest("latest-driver discovery", request);
                }

                var verdicts = await _aiVerifier.VerifyAsync(requests, cancellationToken).ConfigureAwait(true);
                if (verdicts.Count == 0)
                {
                    failedBatches++;
                    _logger.LogWarning(
                        "AI latest-driver discovery returned no usable results for overall rows {Start}-{End}; continuing with the next batch",
                        overallStart,
                        overallEnd);
                }
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
                withoutVerdict += batch.Length;
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
            $"AI latest-driver search complete. {alreadyReviewed.Count + processed} of {Drivers.Count} drivers processed, {found} vendor checks found, {noNewer} already current, {withoutVerdict} no result."
            + (failedBatches > 0 ? $" {failedBatches} batch(es) failed." : string.Empty);
        _logger.LogInformation(
            "AI latest-driver discovery complete: reviewedCandidates={ReviewedCandidates}, discoveryTargets={Targets}, totalDrivers={TotalDrivers}, found={Found}, noNewer={NoNewer}, withoutVerdict={WithoutVerdict}, failedBatches={FailedBatches}",
            alreadyReviewed.Count, targets.Length, Drivers.Count, found, noNewer, withoutVerdict, failedBatches);
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
        if (AmdChipsetSource.IsSupportedAmdChipsetDriver(row.Driver)
            && url.AbsolutePath.Contains("/chipsets/", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation(
                "AI latest-driver lead for {Device} rejected: {Version} is an AMD chipset bundle version, not the version of this individual component. The deterministic AMD source will compare the component manifest instead.",
                row.DeviceName, verdict.LatestKnownVersion ?? candidateVersion.ToString());
            LogAiAdvisorDetails("latest-driver discovery rejected as AMD bundle version", row, verdict);
            return false;
        }
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
        row.Status = DriverStatus.Outdated;
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
    private void OpenSupport()
    {
        _supportWindowOpener?.Open();
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

    private bool CanUpdateAll() => ConfirmedUpdatesCount > 0 || VendorChecksCount > 0;

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
        // No Status filter here: vendor check rows are advisory and usually sit at
        // UpToDate (AI discovery sets them so), yet their row button is enabled via
        // CanUpdate. Filtering by Outdated made that button a silent no-op.
        var pageTargets = targets
            .Where(r => r.AvailableUpdate is { InstallKind: UpdateInstallKind.VendorPage })
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

        // Switch the grid to show only rows with available updates so the user does not have
        // to scrub through 250 unrelated entries to follow the active driver. The
        // user can pick a different filter later.
        UpdateFilter = DriverUpdateFilter.UpdatesAvailable;

        var runStartedAt = DateTimeOffset.UtcNow;
        var processedUpdateIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var outcomes = new List<(DriverRowViewModel Row, UpdateOperation Operation)>();
        var skipped = new List<(DriverRowViewModel Row, string Reason)>();
        var vendorPageFallbacks = new List<DriverRowViewModel>();
        var installAttemptCount = 0;
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

            installAttemptCount++;
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
                outcomes.AddRange(ApplySharedVendorInstallerResult(finished, row, originalUpdateId));
            }

            if (finished.Status == UpdateStatus.Skipped
                && finished.Candidate.InstallKind == UpdateInstallKind.VendorPage)
            {
                row.Status = DriverStatus.ManualActionRequired;
                if (!dryRun)
                {
                    _logger.LogInformation(
                        "Update run: {Device} could not be updated in-app from its vendor page ({Url}); " +
                        "falling back to opening the page in a browser. Reason: {Reason}. See the 'Vendor page resolve' " +
                        "log lines above for the links found on the page and why none were directly installable.",
                        displayName, finished.Candidate.DownloadUrl,
                        string.IsNullOrWhiteSpace(finished.ErrorMessage) ? "no direct installer found on the page" : finished.ErrorMessage);
                    vendorPageFallbacks.Add(row);
                }
            }
        }

        if (vendorPageFallbacks.Count > 0)
        {
            await OpenVendorPagesAsync(vendorPageFallbacks, cancellationToken).ConfigureAwait(true);
        }

        LogRunSummary(runStartedAt, dryRun, vendorPageFallbacks, installTargets, installAttemptCount, outcomes, skipped);

        StatusText = dryRun
            ? $"Dry run completed for {installTargets.Length} drivers."
            : vendorPageFallbacks.Count > 0
                ? $"Install completed for {installTargets.Length} drivers. Opened {vendorPageFallbacks.Count} vendor pages."
                : includeVendorPages
                    ? $"Install completed for {installTargets.Length} drivers."
                    : $"Install completed for {installTargets.Length} confirmed drivers.";

        if (!dryRun)
        {
            if (_postUpdateSummaryCoordinator is not null)
            {
                StatusText = "Verifying installed drivers and preparing the summary...";
                await _postUpdateSummaryCoordinator.CompleteRunAsync(
                    outcomes.Select(o => o.Operation).ToArray(),
                    report => ApplyPostUpdateVerification(report, outcomes),
                    cancellationToken).ConfigureAwait(true);
                StatusText = "Driver updates checked. Review the summary for the final result.";
            }

            // Save only after Windows verification has reconciled the grid. This prevents
            // an installer exit code from being cached as a successful driver update when
            // Windows still reports the previous version.
            await SaveDriverCacheAsync(cancellationToken).ConfigureAwait(true);

            MaybePromptForRestart(outcomes);
        }
    }

    private void ApplyPostUpdateVerification(
        UpdateVerificationReport report,
        IReadOnlyList<(DriverRowViewModel Row, UpdateOperation Operation)> outcomes)
    {
        var rowsByOperationId = outcomes
            .GroupBy(outcome => outcome.Operation.OperationId)
            .ToDictionary(group => group.Key, group => group.First().Row);

        foreach (var item in report.Items)
        {
            if (!rowsByOperationId.TryGetValue(item.OperationId, out var row))
            {
                _logger.LogWarning(
                    "Post-update verification returned an unknown operation {OperationId} for {Device}",
                    item.OperationId,
                    item.DeviceName);
                continue;
            }

            row.Status = item.Status switch
            {
                UpdateVerificationStatus.VerifiedUpdated => DriverStatus.UpToDate,
                UpdateVerificationStatus.PendingRestart => DriverStatus.RestartRequired,
                UpdateVerificationStatus.NotUpdated => DriverStatus.NotUpdated,
                UpdateVerificationStatus.Failed => DriverStatus.Error,
                UpdateVerificationStatus.ManualActionRequired => DriverStatus.ManualActionRequired,
                UpdateVerificationStatus.Inconclusive => DriverStatus.VerificationInconclusive,
                _ => DriverStatus.Outdated
            };
        }

        RefreshUpdateCounts();
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
        int installAttemptCount,
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
            .Append(", selected rows ").Append(installTargets.Count)
            .Append(", installer attempts ").Append(installAttemptCount)
            .Append(", component outcomes ").Append(outcomes.Count)
            .Append(", vendor pages opened ").Append(vendorPageFallbacks.Count)
            .Append(", succeeded ").Append(succeeded.Length)
            .Append(", failed ").Append(failed.Length)
            .Append(", manual or skipped outcomes ").Append(pipelineSkipped.Length)
            .Append(", covered by shared package ").Append(skipped.Count)
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
            sb.AppendLine("  Covered by a shared package, not separate install attempts:");
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

    private IReadOnlyList<(DriverRowViewModel Row, UpdateOperation Operation)> ApplySharedVendorInstallerResult(
        UpdateOperation finished,
        DriverRowViewModel masterRow,
        string originalUpdateId)
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
        var sharedOutcomes = new List<(DriverRowViewModel Row, UpdateOperation Operation)>();
        foreach (var row in Drivers.Where(r =>
            r.AvailableUpdate?.SourceUpdateId is { } id
            && (string.Equals(id, finished.Candidate.SourceUpdateId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(id, originalUpdateId, StringComparison.OrdinalIgnoreCase))))
        {
            var rowOperation = ReferenceEquals(row, masterRow)
                ? finished
                : finished with
                {
                    OperationId = Guid.NewGuid(),
                    TargetSnapshot = row.Driver,
                    Candidate = finished.Candidate with
                    {
                        ForHardwareId = row.AvailableUpdate!.ForHardwareId,
                        NewVersion = row.AvailableUpdate.NewVersion,
                        NewDate = row.AvailableUpdate.NewDate,
                        Confidence = row.AvailableUpdate.Confidence,
                        AiVerification = row.AvailableUpdate.AiVerification
                    }
                };
            row.Status = MapOperationStatus(finished.Status);
            row.LastOperation = rowOperation;
            if (finished.Status == UpdateStatus.Succeeded || !ReferenceEquals(row, masterRow))
            {
                row.AvailableUpdate = null;
            }
            if (!ReferenceEquals(row, masterRow))
            {
                sharedOutcomes.Add((row, rowOperation));
            }
        }

        RefreshUpdateCounts();
        return sharedOutcomes;
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
        DriverUpdateFilter.AllDrivers => true,
        DriverUpdateFilter.UpdatesAvailable => row.AvailableUpdate is not null,
        DriverUpdateFilter.NoUpdateAvailable => row.AvailableUpdate is null,
        _ => true
    };
}
