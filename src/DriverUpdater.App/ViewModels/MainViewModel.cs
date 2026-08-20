using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DriverUpdater.App.Ai;
using DriverUpdater.App.Logging;
using DriverUpdater.App.Services;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using DriverUpdater.Core.Options;
using DriverUpdater.Services.Install;
using DriverUpdater.Services.Scanning;
using DriverUpdater.Services.Sources;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DriverUpdater.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private const int VendorVerificationAttempts = 20;
    private const int AiDiscoveryBatchSize = 20;
    private const int AiDiscoveryParallelism = 3;
    private static readonly TimeSpan VendorVerificationInterval = TimeSpan.FromSeconds(3);

    private readonly IDriverScanService _scanService;
    private readonly IReadOnlyList<IUpdateSource> _updateSources;
    private readonly IOemDetectionService _oemDetectionService;
    private readonly IInstallPipeline _installPipeline;
    private readonly IInstallConfirmation _installConfirmation;
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
    private readonly IMachineProfileProvider? _machineProfileProvider;
    private readonly IOptionsMonitor<AiSettings>? _aiSettings;
    private readonly IOptionsMonitor<ScheduleSettings>? _scheduleSettings;
    private readonly IAiScanConfirmation? _aiScanConfirmation;
    private readonly IPostUpdateSummaryCoordinator? _postUpdateSummaryCoordinator;
    private readonly ISupportWindowOpener? _supportWindowOpener;
    private readonly IVendorPageInstallerResolver? _vendorPageResolver;
    private readonly IChatSettingsApplier? _chatSettingsApplier;
    private readonly IDriverUpdateExclusionStore? _exclusionStore;
    private readonly InMemoryLogSink? _logSink;
    private readonly IDriverDowngradeService? _downgradeService;
    private readonly IDriverVersionHistoryWindowOpener? _versionHistoryWindowOpener;
    private readonly IRestorePointService? _restorePointService;
    private readonly Dispatcher _dispatcher;

    // True while RunUpdatesAsync is installing/verifying. Lets the driver chat tell the AI that
    // the log tail reflects a live update run rather than a finished one.
    private int _activeUpdateRuns;

    // Devices opted in to unattended updating. Mirrors the auto-update selection store so a row
    // created mid-scan can be initialised without another disk read.
    private CancellationTokenSource? _aiSearchCancellation;
    private CancellationTokenSource? _scanCancellation;
    private bool _skipAiSearchRequested;
    private bool _driverCacheClearPending;

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

    // Devices the user excluded from updating altogether. Mirrors the exclusion store so a row
    // created mid-scan can be marked without another disk read.
    private HashSet<string> _excludedDeviceIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<MainViewModel> _logger;

    public ObservableCollection<DriverRowViewModel> Drivers { get; } = new();

    public ICollectionView DriversView { get; }

    public IReadOnlyList<DriverCategory> AvailableCategories { get; } =
        Enum.GetValues<DriverCategory>().ToArray();

    public IReadOnlyList<DriverUpdateFilterOption> AvailableUpdateFilters { get; } =
    [
        new(DriverUpdateFilter.AllDrivers, "All drivers"),
        new(DriverUpdateFilter.UpdatesAvailable, "Updates available"),
        new(DriverUpdateFilter.NoUpdateAvailable, "No update available"),
        new(DriverUpdateFilter.ExcludedDrivers, "Excluded from updates"),
        new(DriverUpdateFilter.ExcludedWithUpdates, "Excluded with updates")
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    private string _statusText = "Ready. Click Scan to inventory drivers.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(ScanWithAiCommand))]
    [NotifyCanExecuteChangedFor(nameof(ScanCancelCommand))]
    [NotifyCanExecuteChangedFor(nameof(UpdateWithAiCommand))]
    private bool _isScanning;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(ScanWithAiCommand))]
    [NotifyCanExecuteChangedFor(nameof(UpdateWithAiCommand))]
    [NotifyCanExecuteChangedFor(nameof(UpdateAllCommand))]
    private bool _isUpdatingWithAi;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    private int _scannedCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    private bool _isShowingCachedDrivers;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    [NotifyCanExecuteChangedFor(nameof(UpdateAllCommand))]
    private int _updatesFoundCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    private int _excludedUpdatesFoundCount;

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
    [NotifyCanExecuteChangedFor(nameof(SkipAiSearchCommand))]
    private bool _isAiSearchRunning;

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

    private string ExcludedUpdatesProgressSuffix => ExcludedUpdatesFoundCount switch
    {
        0 => string.Empty,
        1 => ", 1 excluded update",
        _ => $", {ExcludedUpdatesFoundCount} excluded updates"
    };

    public string ProgressText => IsScanning
        ? $"Scanning... {ScannedCount} drivers found"
        : ScannedCount > 0
            ? IsShowingCachedDrivers
                ? $"{ScannedCount} cached drivers, {UpdatesFoundCount} update{(UpdatesFoundCount == 1 ? string.Empty : "s")} available"
                  + ExcludedUpdatesProgressSuffix
                  + " (scan to refresh)"
                : $"{ScannedCount} drivers, {UpdatesFoundCount} update{(UpdatesFoundCount == 1 ? string.Empty : "s")} available"
                  + ExcludedUpdatesProgressSuffix
                  + (UpdatesFoundCount > 0 ? $" ({ConfirmedUpdatesCount} confirmed, {VendorChecksCount} likely)" : string.Empty)
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
        ISupportWindowOpener? supportWindowOpener = null,
        IOptionsMonitor<AiSettings>? aiSettings = null,
        IAiScanConfirmation? aiScanConfirmation = null,
        IVendorPageInstallerResolver? vendorPageResolver = null,
        IChatSettingsApplier? chatSettingsApplier = null,
        IDriverUpdateExclusionStore? exclusionStore = null,
        InMemoryLogSink? logSink = null,
        IDriverDowngradeService? downgradeService = null,
        IDriverVersionHistoryWindowOpener? versionHistoryWindowOpener = null,
        IRestorePointService? restorePointService = null,
        IMachineProfileProvider? machineProfileProvider = null,
        IOptionsMonitor<ScheduleSettings>? scheduleSettings = null)
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
        _machineProfileProvider = machineProfileProvider;
        _scanService = scanService;
        _updateSources = updateSources.ToArray();
        _oemDetectionService = oemDetectionService;
        _installPipeline = installPipeline;
        _installConfirmation = installConfirmation;
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
        _aiSettings = aiSettings;
        _scheduleSettings = scheduleSettings;
        _aiScanConfirmation = aiScanConfirmation;
        _postUpdateSummaryCoordinator = postUpdateSummaryCoordinator;
        _supportWindowOpener = supportWindowOpener;
        _vendorPageResolver = vendorPageResolver;
        _chatSettingsApplier = chatSettingsApplier;
        _exclusionStore = exclusionStore;
        _logSink = logSink;
        _downgradeService = downgradeService;
        _versionHistoryWindowOpener = versionHistoryWindowOpener;
        _restorePointService = restorePointService;
        _logger = logger;
        _dispatcher = Dispatcher.CurrentDispatcher;

        if (_driverCacheStore is not null)
        {
            _driverCacheStore.Cleared += OnDriverCacheCleared;
        }

        DriversView = CollectionViewSource.GetDefaultView(Drivers);
        DriversView.Filter = FilterDriver;

        DriverChatMessages.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasDriverChat));
            OnPropertyChanged(nameof(HasNoDriverChat));
        };

        ResetChatSuggestions();

        SetChatLanguageToggleWithoutApplying(
            (_aiSettings?.CurrentValue.ResponseLanguage ?? AppLanguage.English) == AppLanguage.Hebrew);
        _aiSettings?.OnChange(settings => _dispatcher.BeginInvoke(() =>
            SetChatLanguageToggleWithoutApplying(settings.ResponseLanguage == AppLanguage.Hebrew)));
    }

    // ----- AI chat about the scanned drivers -----

    public ObservableCollection<LogChatMessage> DriverChatMessages { get; } = new();

    /// <summary>Toggles the driver AI chat panel (the sparkle button). Closed by default.</summary>
    [ObservableProperty]
    private bool _isDriverChatVisible;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendDriverChatCommand))]
    [NotifyCanExecuteChangedFor(nameof(AskWhyAiRecommendedCommand))]
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
            .Where(r => r.HasAvailableUpdate
                && r.Driver.HardwareIds.Any(id =>
                    hardwareIds.Contains(id, StringComparer.OrdinalIgnoreCase)))
            .ToArray();

    private bool CanSendDriverChat() => !IsDriverChatting && !string.IsNullOrWhiteSpace(DriverChatInput);

    private bool CanAskWhyAiRecommended(LogChatMessage? message) =>
        !IsDriverChatting && message?.RecommendedHardwareIds is { Count: > 0 };

    [RelayCommand(CanExecute = nameof(CanAskWhyAiRecommended))]
    private async Task AskWhyAiRecommendedAsync(LogChatMessage? message, CancellationToken cancellationToken)
    {
        if (message?.RecommendedHardwareIds is not { Count: > 0 } ids)
        {
            return;
        }

        var rows = MatchRecommendedRows(ids);
        if (rows.Length == 0)
        {
            StatusText = "The recommended updates are no longer available. Rescan and ask again.";
            return;
        }

        var responseLanguage = message.ResponseLanguage
            ?? _aiSettings?.CurrentValue.ResponseLanguage
            ?? AppLanguage.English;
        var deviceList = string.Join(", ", rows.Select(static row => $"{row.DeviceName} ({row.HardwareId})"));
        var question = responseLanguage == AppLanguage.Hebrew
            ? $"למה המלצת לי לעדכן את מנהלי ההתקנים הבאים: {deviceList}? הסבר את השיקולים, התועלת, הסיכונים ורמת הוודאות לגבי כל אחד מהם."
            : $"Why did you recommend updating these drivers: {deviceList}? Explain the evidence, benefit, risks, and uncertainty for each one.";
        var displayQuestion = responseLanguage == AppLanguage.Hebrew
            ? "למה המלצת על העדכונים האלה?"
            : "Why did you recommend these updates?";

        await SendDriverChatQuestionAsync(
            question,
            allowInstallActions: false,
            cancellationToken,
            displayQuestion,
            responseLanguage).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanSendDriverChat), IncludeCancelCommand = true)]
    private async Task SendDriverChatAsync(CancellationToken cancellationToken)
    {
        var question = DriverChatInput?.Trim();
        if (string.IsNullOrWhiteSpace(question))
        {
            return;
        }
        DriverChatInput = string.Empty;
        await SendDriverChatQuestionAsync(question, allowInstallActions: true, cancellationToken).ConfigureAwait(true);
    }

    private async Task SendDriverChatQuestionAsync(
        string question,
        bool allowInstallActions,
        CancellationToken cancellationToken,
        string? displayQuestion = null,
        AppLanguage? responseLanguageOverride = null)
    {
        if (_driverChatCompleter is null || !_driverChatCompleter.IsConfigured)
        {
            DriverChatMessages.Add(new LogChatMessage(IsUser: false,
                "AI is not configured. Open Settings > AI to enable it, then ask again."));
            return;
        }

        var context = BuildDriverChatContext();
        var history = DriverChatMessages.Where(m => !string.IsNullOrWhiteSpace(m.Text)).ToArray();
        DriverChatMessages.Add(new LogChatMessage(IsUser: true, displayQuestion ?? question));
        IsDriverChatting = true;
        StatusText = "Asking AI about your drivers...";
        try
        {
            var responseLanguage = responseLanguageOverride
                ?? _aiSettings?.CurrentValue.ResponseLanguage
                ?? AppLanguage.English;
            var currentSettings = await LoadChatSettingsAsync(cancellationToken).ConfigureAwait(true);
            // Only Gemini declares the google_search tool; promising search to a local Ollama
            // model would invite invented "I searched the web" claims.
            var webSearchEnabled = _driverChatCompleter.Provider == AiProvider.Gemini
                && (_aiSettings?.CurrentValue.EnableWebSearch ?? true);
            var prompt = DriverChatPromptBuilder.Build(
                context,
                history,
                question,
                responseLanguage,
                allowInstallActions,
                currentSettings,
                recentLogs: _logSink?.Snapshot(),
                updateRunInProgress: _activeUpdateRuns > 0,
                webSearchEnabled: webSearchEnabled,
                machine: await ReadMachineProfileAsync(cancellationToken).ConfigureAwait(true));
            var answer = await _driverChatCompleter.CompleteAsync(prompt, cancellationToken).ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(answer))
            {
                DriverChatMessages.Add(new LogChatMessage(IsUser: false,
                    "(No response from AI. Check the AI provider in Settings and try again.)"));
                StatusText = "AI did not return an answer.";
                return;
            }

            var (text, recommendedIds, requestsScan, settingChanges) = DriverChatActionParser.Parse(answer);
            var matched = allowInstallActions
                ? MatchRecommendedRows(recommendedIds)
                : Array.Empty<DriverRowViewModel>();
            var hasNoAvailableUpdates = context.All(static driver =>
                string.IsNullOrWhiteSpace(driver.AvailableVersion));
            var rejectedUnavailableRecommendations = allowInstallActions
                && recommendedIds.Count > 0
                && matched.Length == 0
                && hasNoAvailableUpdates;
            if (rejectedUnavailableRecommendations)
            {
                text = string.Empty;
            }
            var showScanAction = allowInstallActions
                && matched.Length == 0
                && hasNoAvailableUpdates
                && (requestsScan || rejectedUnavailableRecommendations);
            var proposedSettings = allowInstallActions && _chatSettingsApplier is not null
                ? settingChanges
                : Array.Empty<ChatSettingChange>();
            var questionLanguage = DetectResponseLanguage(displayQuestion ?? question, responseLanguage);
            var actualResponseLanguage = DetectResponseLanguage(text, questionLanguage);
            if (!string.IsNullOrWhiteSpace(text))
            {
                DriverChatMessages.Add(new LogChatMessage(
                    IsUser: false,
                    text,
                    ResponseLanguage: actualResponseLanguage));
            }
            else if (showScanAction)
            {
                var noUpdatesText = actualResponseLanguage == AppLanguage.Hebrew
                    ? "אני לא רואה כרגע עדכוני מנהלי התקנים זמינים בסריקה הנוכחית. אפשר לבצע סריקה חדשה כדי לרענן את הרשימה."
                    : "I do not currently see available driver updates in this scan. You can run a new scan to refresh the list.";
                DriverChatMessages.Add(new LogChatMessage(
                    IsUser: false,
                    noUpdatesText,
                    ResponseLanguage: actualResponseLanguage));
            }
            else if (matched.Length == 0 && proposedSettings.Count == 0)
            {
                DriverChatMessages.Add(new LogChatMessage(IsUser: false, answer.Trim()));
            }

            if (proposedSettings.Count > 0)
            {
                DriverChatMessages.Add(new LogChatMessage(
                    IsUser: false,
                    Text: string.Empty,
                    ResponseLanguage: actualResponseLanguage,
                    SettingProposal: new ChatSettingProposalViewModel(proposedSettings, actualResponseLanguage)));
            }

            if (matched.Length > 0)
            {
                DriverChatMessages.Add(new LogChatMessage(IsUser: false, string.Empty,
                    matched.Select(r => r.HardwareId).ToArray(),
                    ResponseLanguage: actualResponseLanguage));
                StatusText = $"AI recommends installing {matched.Length} update(s). Press the button in the chat.";
            }
            else if (showScanAction)
            {
                DriverChatMessages.Add(new LogChatMessage(
                    IsUser: false,
                    Text: string.Empty,
                    ShowScanAction: true,
                    ResponseLanguage: actualResponseLanguage));
                StatusText = "AI does not see available updates in the current scan. Press Scan now to refresh the list.";
            }
            else if (proposedSettings.Count > 0)
            {
                StatusText = proposedSettings.Count == 1
                    ? "AI suggests a settings change. Confirm it in the chat."
                    : $"AI suggests {proposedSettings.Count} settings changes. Confirm them in the chat.";
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

    private static AppLanguage DetectResponseLanguage(string text, AppLanguage fallback)
    {
        if (text.Any(static character => character is >= '\u0590' and <= '\u05ff'))
        {
            return AppLanguage.Hebrew;
        }

        return text.Any(static character => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z')
            ? AppLanguage.English
            : fallback;
    }

    [RelayCommand]
    private void ClearDriverChat()
    {
        DriverChatMessages.Clear();
        ResetChatSuggestions();
        StatusText = "Driver chat cleared.";
    }

    // ----- Settings the AI proposes changing -----

    private async Task<AppSettings?> LoadChatSettingsAsync(CancellationToken cancellationToken)
    {
        if (_chatSettingsApplier is null)
        {
            return null;
        }

        try
        {
            return await _chatSettingsApplier.LoadAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Without the settings block the chat still answers about drivers, so a read
            // failure must not take the whole turn down.
            _logger.LogWarning(ex, "Driver chat could not read the current settings for the prompt");
            return null;
        }
    }

    private static bool CanApplyChatSettings(ChatSettingProposalViewModel? proposal) =>
        proposal is { IsPending: true };

    [RelayCommand(CanExecute = nameof(CanApplyChatSettings))]
    private async Task ApplyChatSettingsAsync(
        ChatSettingProposalViewModel? proposal,
        CancellationToken cancellationToken)
    {
        if (proposal is not { IsPending: true })
        {
            return;
        }

        if (_chatSettingsApplier is null)
        {
            proposal.MarkFailed("settings cannot be changed from the chat in this build");
            return;
        }

        try
        {
            var result = await _chatSettingsApplier
                .ApplyAsync(proposal.Changes, cancellationToken)
                .ConfigureAwait(true);
            if (result.Succeeded)
            {
                proposal.MarkApplied(result.Warning);
                StatusText = "Settings updated from the AI chat.";
            }
            else
            {
                proposal.MarkFailed(result.Warning ?? "unknown error");
                StatusText = "The AI settings change could not be applied.";
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "The AI settings change was cancelled.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Applying an AI-proposed settings change failed");
            proposal.MarkFailed(ex.Message);
            StatusText = $"The AI settings change failed: {ex.Message}";
        }
        finally
        {
            ApplyChatSettingsCommand.NotifyCanExecuteChanged();
            DeclineChatSettingsCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanApplyChatSettings))]
    private void DeclineChatSettings(ChatSettingProposalViewModel? proposal)
    {
        if (proposal is not { IsPending: true })
        {
            return;
        }

        proposal.MarkDeclined();
        StatusText = "The AI settings change was declined.";
        ApplyChatSettingsCommand.NotifyCanExecuteChanged();
        DeclineChatSettingsCommand.NotifyCanExecuteChanged();
    }

    // ----- Quick Hebrew/English toggle next to the chat header -----

    private bool _syncingChatLanguageToggle;

    [ObservableProperty] private bool _isAiResponseLanguageHebrew;

    [ObservableProperty] private bool _isChangingChatLanguage;

    partial void OnIsAiResponseLanguageHebrewChanged(bool value)
    {
        if (_syncingChatLanguageToggle)
        {
            return;
        }

        _ = ApplyChatLanguageToggleAsync(value);
    }

    private async Task ApplyChatLanguageToggleAsync(bool wantsHebrew)
    {
        if (_chatSettingsApplier is null
            || !ChatSettingCatalog.TryResolve("ai.language", wantsHebrew ? "hebrew" : "english", out var change))
        {
            SetChatLanguageToggleWithoutApplying(!wantsHebrew);
            return;
        }

        IsChangingChatLanguage = true;
        try
        {
            var result = await _chatSettingsApplier.ApplyAsync([change]).ConfigureAwait(true);
            if (result.Succeeded)
            {
                StatusText = wantsHebrew
                    ? "AI chat will answer in Hebrew."
                    : "AI chat will answer in English.";
            }
            else
            {
                StatusText = $"Could not change the AI chat language: {result.Warning ?? "unknown error"}";
                SetChatLanguageToggleWithoutApplying(!wantsHebrew);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Toggling the AI chat language failed");
            StatusText = $"Could not change the AI chat language: {ex.Message}";
            SetChatLanguageToggleWithoutApplying(!wantsHebrew);
        }
        finally
        {
            IsChangingChatLanguage = false;
        }
    }

    private void SetChatLanguageToggleWithoutApplying(bool isHebrew)
    {
        _syncingChatLanguageToggle = true;
        IsAiResponseLanguageHebrew = isHebrew;
        _syncingChatLanguageToggle = false;
    }

    // ----- Drifting conversation starters -----

    private readonly Random _suggestionRandom = new();
    private DispatcherTimer? _suggestionTimer;
    private int _suggestionSlot;

    /// <summary>How long one chip stays on screen before it is swapped for a new one.</summary>
    public static TimeSpan SuggestionRotationInterval => TimeSpan.FromSeconds(8);

    public ObservableCollection<ChatSuggestion> ChatSuggestions { get; } = new();

    private AppLanguage SuggestionLanguage =>
        _aiSettings?.CurrentValue.ResponseLanguage ?? AppLanguage.English;

    /// <summary>
    /// Starts the row over with a single chip. The rest fade in one tick apart, which is what
    /// keeps every chip's 24-second fade-out landing exactly on the tick that replaces it.
    /// </summary>
    public void ResetChatSuggestions()
    {
        ChatSuggestions.Clear();
        _suggestionSlot = 0;
        var first = PickSuggestion();
        if (first is not null)
        {
            ChatSuggestions.Add(first);
        }
    }

    /// <summary>
    /// Grows the row to its full size one chip per call, then keeps replacing a single chip
    /// per call, round-robin, so it changes slowly enough to read instead of all at once.
    /// </summary>
    public void AdvanceChatSuggestions()
    {
        var next = PickSuggestion();
        if (next is null)
        {
            return;
        }

        if (ChatSuggestions.Count < ChatSuggestionCatalog.VisibleCount)
        {
            ChatSuggestions.Add(next);
            return;
        }

        var slot = _suggestionSlot % ChatSuggestions.Count;
        _suggestionSlot = (slot + 1) % ChatSuggestions.Count;

        // Assigning over the slot would keep the same ItemsControl container alive, so the chip
        // template's Loaded trigger never fires again and the new text stays stuck on the last
        // frame of the previous chip's fade-out - invisible, but still clickable. Removing and
        // inserting forces a fresh container that starts the fade from zero.
        ChatSuggestions.RemoveAt(slot);
        ChatSuggestions.Insert(slot, next);
    }

    private ChatSuggestion? PickSuggestion()
    {
        var candidates = ChatSuggestionCatalog
            .For(SuggestionLanguage)
            .Where(suggestion => !ChatSuggestions.Contains(suggestion))
            .ToArray();
        return candidates.Length == 0 ? null : candidates[_suggestionRandom.Next(candidates.Length)];
    }

    private bool CanUseChatSuggestion(ChatSuggestion? suggestion) =>
        !IsDriverChatting && suggestion is not null;

    [RelayCommand(CanExecute = nameof(CanUseChatSuggestion))]
    private async Task UseChatSuggestionAsync(ChatSuggestion? suggestion, CancellationToken cancellationToken)
    {
        if (suggestion is null || IsDriverChatting)
        {
            return;
        }

        DriverChatInput = string.Empty;
        await SendDriverChatQuestionAsync(suggestion.Text, allowInstallActions: true, cancellationToken)
            .ConfigureAwait(true);
    }

    partial void OnIsDriverChattingChanged(bool value) =>
        UseChatSuggestionCommand.NotifyCanExecuteChanged();

    partial void OnIsDriverChatVisibleChanged(bool value)
    {
        if (value)
        {
            ResetChatSuggestions();
            StartSuggestionRotation();
        }
        else
        {
            StopSuggestionRotation();
        }
    }

    private void StartSuggestionRotation()
    {
        if (_suggestionTimer is not null)
        {
            _suggestionTimer.Start();
            return;
        }

        _suggestionTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = SuggestionRotationInterval
        };
        _suggestionTimer.Tick += (_, _) => AdvanceChatSuggestions();
        _suggestionTimer.Start();
    }

    private void StopSuggestionRotation() => _suggestionTimer?.Stop();

    private IReadOnlyList<DriverChatContextItem> BuildDriverChatContext() =>
        Drivers.Select(r =>
        {
            var actionableUpdate = r.HasAvailableUpdate ? r.AvailableUpdate : null;
            return new DriverChatContextItem(
                DeviceName: r.DeviceName,
                HardwareId: r.HardwareId,
                Category: r.Category.ToString(),
                CurrentVersion: r.Driver.CurrentVersion?.ToString() ?? r.Driver.CurrentDate?.ToString(),
                Status: r.StatusText,
                AvailableVersion: actionableUpdate?.DisplayVersion,
                AvailableSource: actionableUpdate?.Source.ToString());
        }).ToList();

    partial void OnCategoryFilterChanged(DriverCategory? value) => DriversView.Refresh();
    partial void OnUpdateFilterChanged(DriverUpdateFilter value) => DriversView.Refresh();
    partial void OnSearchTextChanged(string value) => DriversView.Refresh();

    private void AddDriverRow(DriverRowViewModel row) => Drivers.Add(row);

    private void ClearDriverRows() => Drivers.Clear();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            DetectedOem = await _oemDetectionService.DetectAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StatusText = "App update cancelled.";
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

            if (!await LoadExcludedDriversAsync(cancellationToken).ConfigureAwait(true))
            {
                return;
            }

            var staleDropped = 0;
            foreach (var entry in snapshot.Entries)
            {
                // An excluded device keeps its cached result on show - the point is to see what
                // was found - but the row is marked so nothing offers to install it.
                var excluded = IsExcluded(entry.Driver);

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
                    IsExcluded = excluded,
                    Status = excluded
                        ? DriverStatus.Excluded
                        : cachedUpdate is null
                            ? entry.Status == DriverStatus.Outdated ? DriverStatus.UpToDate : entry.Status
                            : DriverStatus.VerificationInconclusive,
                    AvailableUpdate = cachedUpdate,
                    IsUpdateFromCache = cachedUpdate is not null,
                    IsScannedThisRun = false
                };
                AddDriverRow(row);
            }

            if (staleDropped > 0)
            {
                _logger.LogInformation(
                    "Dropped {Count} cached update(s) that are no longer newer than the installed driver (stale downgrade guard).",
                    staleDropped);
            }

            ScannedCount = Drivers.Count;
            IsShowingCachedDrivers = true;
            RefreshUpdateCounts();
            StatusText =
                $"Loaded {Drivers.Count} drivers from last scan on {snapshot.CapturedAt.LocalDateTime:yyyy-MM-dd HH:mm}. " +
                "Click Scan to refresh and check for updates.";
            _logger.LogInformation(
                "Loaded {Count} drivers from cache captured at {CapturedAt}",
                Drivers.Count, snapshot.CapturedAt);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
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
            if (_driverCacheClearPending)
            {
                _logger.LogInformation(
                    "Cache clear raced with the final scan save; clearing the newly written snapshot again");
                await _driverCacheStore.ClearAsync(cancellationToken).ConfigureAwait(true);
                return;
            }
            _logger.LogInformation(
                "Main scan cache save completed: {DriverCount} drivers, {FreshCount} fresh update result(s), {FallbackCount} cached fallback result(s)",
                entries.Length,
                Drivers.Count(row => row.AvailableUpdate is not null && !row.IsUpdateFromCache),
                Drivers.Count(row => row.AvailableUpdate is not null && row.IsUpdateFromCache));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save the driver cache");
        }
    }

    private void OnDriverCacheCleared(object? sender, EventArgs e)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.Invoke(() => OnDriverCacheCleared(sender, e));
            return;
        }

        _driverCacheClearPending = true;
        if (IsScanning)
        {
            StatusText = "Driver cache cleared. The current scan results will be discarded.";
            _logger.LogInformation(
                "Driver cache was cleared during a scan; current in-memory results will be discarded before they can be saved");
            return;
        }

        ApplyDriverCacheClearToMainView();
    }

    private void ApplyDriverCacheClearToMainView()
    {
        var driverCount = Drivers.Count;
        var updateCount = Drivers.Count(row => row.AvailableUpdate is not null);
        ClearDriverRows();
        ScannedCount = 0;
        IsShowingCachedDrivers = false;
        UpdatesFoundCount = 0;
        ExcludedUpdatesFoundCount = 0;
        ConfirmedUpdatesCount = 0;
        VendorChecksCount = 0;
        _driverCacheClearPending = false;
        StatusText = "Driver update cache cleared. Run Scan to search from scratch.";
        _logger.LogInformation(
            "Driver cache clear applied to the main view: removed {DriverCount} cached driver row(s) and {UpdateCount} update result(s)",
            driverCount,
            updateCount);
    }

    private bool DiscardScanIfCacheWasCleared()
    {
        if (!_driverCacheClearPending)
        {
            return false;
        }

        ApplyDriverCacheClearToMainView();
        _logger.LogInformation(
            "Current scan discarded because the driver cache was cleared while the scan was running");
        return true;
    }

    [RelayCommand(CanExecute = nameof(CanScan))]
    private Task ScanAsync() => RunScanAsync(includeAi: false);

    [RelayCommand(CanExecute = nameof(CanScan))]
    private Task ScanWithAiAsync()
    {
        if (_aiVerifier?.IsConfigured != true)
        {
            StatusText = "Configure an AI provider in Settings before using Scan with AI.";
            return Task.CompletedTask;
        }

        return RunScanAsync(includeAi: true);
    }

    [RelayCommand(CanExecute = nameof(CanCancelScan))]
    private void ScanCancel() => _scanCancellation?.Cancel();

    // One click for the whole loop: scan, let the AI research the hardware on the web, then
    // install exactly what it endorsed. Nothing is asked of the user between the click and the
    // install confirmation, so an update the AI could not rate is left alone rather than
    // guessed at.
    [RelayCommand(CanExecute = nameof(CanUpdateWithAi))]
    private async Task UpdateWithAiAsync(CancellationToken cancellationToken)
    {
        if (_aiVerifier?.IsConfigured != true)
        {
            StatusText = "Configure an AI provider in Settings before using Update with AI.";
            return;
        }

        IsUpdatingWithAi = true;
        try
        {
            var tolerance = _scheduleSettings?.CurrentValue.AiRiskTolerance ?? AiAutoUpdateRiskTolerance.SafeOnly;
            _logger.LogInformation(
                "Update with AI started: provider={Provider}, risk tolerance={Tolerance}",
                _aiVerifier.Provider, tolerance);

            if (!await RunScanAsync(includeAi: true).ConfigureAwait(true))
            {
                StatusText = "Update with AI stopped: the AI scan did not finish, so nothing was installed.";
                return;
            }

            var endorsed = SelectAiEndorsedRows(tolerance);
            if (endorsed.Count == 0)
            {
                StatusText = UpdatesFoundCount == 0
                    ? "Update with AI: the AI found nothing to update."
                    : $"Update with AI: the AI did not endorse any of the {UpdatesFoundCount} available update(s) "
                      + $"at the {AiUpdateRiskPolicy.Describe(tolerance)} risk tolerance. Nothing was installed.";
                return;
            }

            StatusText = $"Update with AI: installing {endorsed.Count} update(s) the AI endorsed...";
            await RunUpdatesAsync(endorsed, dryRun: false, includeVendorPages: true, cancellationToken)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Update with AI cancelled.";
            _logger.LogInformation("Update with AI cancelled");
        }
        finally
        {
            IsUpdatingWithAi = false;
        }
    }

    private bool CanUpdateWithAi() => !IsScanning && !IsUpdatingWithAi;

    // Every row the AI actually vouched for. A row without a verdict is never installed here:
    // "no answer" is not an endorsement, and the user is not being asked to fill the gap.
    private IReadOnlyList<DriverRowViewModel> SelectAiEndorsedRows(AiAutoUpdateRiskTolerance tolerance)
    {
        var endorsed = new List<DriverRowViewModel>();
        foreach (var row in Drivers)
        {
            if (!row.CanUpdate)
            {
                continue;
            }

            var verdict = row.AvailableUpdate?.AiVerification;
            if (verdict is null)
            {
                _logger.LogInformation(
                    "Update with AI: skipping {Device} - the AI returned no verdict for this update",
                    DriverDisplayName(row));
                continue;
            }

            if (!verdict.IsGenuinelyNewer)
            {
                _logger.LogInformation(
                    "Update with AI: skipping {Device} - the AI does not consider this a genuine upgrade. {Summary}",
                    DriverDisplayName(row), verdict.Summary);
                continue;
            }

            if (!AiUpdateRiskPolicy.IsWithinTolerance(verdict.Risk, tolerance))
            {
                _logger.LogInformation(
                    "Update with AI: skipping {Device} - risk rated {Risk}, above the configured tolerance {Tolerance}. {Summary}",
                    DriverDisplayName(row), verdict.Risk, tolerance, verdict.Summary);
                continue;
            }

            _logger.LogInformation(
                "Update with AI: installing {Device} - risk rated {Risk}, target {Version}. {Summary}",
                DriverDisplayName(row), verdict.Risk, row.AvailableUpdate!.DisplayVersion, verdict.Summary);
            endorsed.Add(row);
        }

        _logger.LogInformation(
            "Update with AI selection: {Selected} of {Available} available update(s) endorsed at tolerance {Tolerance}",
            endorsed.Count, UpdatesFoundCount, tolerance);
        return endorsed;
    }

    private async Task<bool> RunScanAsync(bool includeAi)
    {
        using var scanCancellation = new CancellationTokenSource();
        _scanCancellation = scanCancellation;
        var cancellationToken = scanCancellation.Token;
        IsScanning = true;
        var previousRows = SnapshotRowsByDeviceId();
        ClearDriverRows();
        ScannedCount = 0;
        IsShowingCachedDrivers = false;
        UpdatesFoundCount = 0;
        ExcludedUpdatesFoundCount = 0;
        ConfirmedUpdatesCount = 0;
        VendorChecksCount = 0;
        StatusText = "Scanning drivers via WMI...";
        var stopwatch = Stopwatch.StartNew();

        try
        {
            await foreach (var driver in _scanService.ScanAsync(cancellationToken))
            {
                AddDriverRow(new DriverRowViewModel(driver));
                ScannedCount = Drivers.Count;
            }

            if (DiscardScanIfCacheWasCleared())
            {
                return false;
            }

            var elapsed = stopwatch.Elapsed;
            StatusText =
                $"Inventory scan complete. {Drivers.Count} current drivers found in {elapsed.TotalSeconds:F1}s. "
                + "Checking all of them for updates...";
            _logger.LogInformation(
                "Inventory scan finished in {Elapsed}: {Count} current driver(s) found; checking all of them for updates",
                elapsed,
                Drivers.Count);

            if (!await QueryUpdateSourcesAsync(cancellationToken))
            {
                return false;
            }
            if (DiscardScanIfCacheWasCleared())
            {
                return false;
            }
            RestorePendingUpdates(previousRows);

            if (includeAi && _aiVerifier?.IsConfigured == true)
            {
                var estimate = BuildAiScanUsageEstimate();
                var approved = _aiVerifier.Provider != AiProvider.Gemini
                    || _aiScanConfirmation is null
                    || await _aiScanConfirmation.ConfirmAsync(estimate, cancellationToken).ConfigureAwait(true);
                if (approved)
                {
                    StatusText =
                        $"Scan complete. {Drivers.Count} drivers found. Starting AI post-scan verification...";
                    await RunAiPostScanAsync(cancellationToken);
                }
                else
                {
                    _logger.LogInformation(
                        "AI stage skipped after usage warning; deterministic scan results will be kept");
                    StatusText = "AI scan was not approved. Keeping regular scan results.";
                }
            }

            if (DiscardScanIfCacheWasCleared())
            {
                return false;
            }

            await ResolveVendorPageCandidatesAsync(cancellationToken).ConfigureAwait(true);
            if (DiscardScanIfCacheWasCleared())
            {
                return false;
            }

            FinalizeScanStatuses();
            LogScanSummary();

            StatusText = UpdatesFoundCount == 0
                ? $"Done. {Drivers.Count} drivers, no updates available."
                : $"Done. {Drivers.Count} drivers, {UpdatesFoundCount} update{(UpdatesFoundCount == 1 ? string.Empty : "s")} available "
                  + $"({ConfirmedUpdatesCount} confirmed, {VendorChecksCount} likely).";
            if (DiscardScanIfCacheWasCleared())
            {
                return false;
            }
            await SaveDriverCacheAsync(cancellationToken).ConfigureAwait(true);
            return !DiscardScanIfCacheWasCleared();
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
            _aiSearchCancellation?.Cancel();
            _aiSearchCancellation?.Dispose();
            _aiSearchCancellation = null;
            IsAiSearchRunning = false;
            if (ReferenceEquals(_scanCancellation, scanCancellation))
            {
                _scanCancellation = null;
            }
            IsScanning = false;
        }

        return false;
    }

    private AiScanUsageEstimate BuildAiScanUsageEstimate()
    {
        var scannedRows = Drivers.Where(row => row.IsScannedThisRun).ToArray();
        var candidateRows = scannedRows.Count(row => row.HasAvailableUpdate);
        var discoveryRows = scannedRows.Length - candidateRows;
        var plannedRequests = (candidateRows > 0 ? 1 : 0)
            + (int)Math.Ceiling(discoveryRows / (double)AiDiscoveryBatchSize);
        var model = _aiSettings?.CurrentValue.GeminiModel;
        return new AiScanUsageEstimate(
            scannedRows.Length,
            plannedRequests,
            string.IsNullOrWhiteSpace(model) ? "Gemini" : model);
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

    private void RestorePendingUpdates(IReadOnlyDictionary<string, DriverRowViewModel> previousRows)
    {
        var fresh = 0;
        var revalidated = 0;
        var replaced = 0;
        var restored = 0;
        var dropped = 0;
        foreach (var row in Drivers.Where(r => r.IsScannedThisRun))
        {
            previousRows.TryGetValue(row.Driver.DeviceId, out var previous);
            var pending = previous?.AvailableUpdate;
            if (!row.IsUpdateFromCache && row.AvailableUpdate is { } freshCandidate)
            {
                fresh++;
                if (pending is null)
                {
                    continue;
                }

                if (string.Equals(
                        freshCandidate.SourceUpdateId,
                        pending.SourceUpdateId,
                        StringComparison.OrdinalIgnoreCase)
                    && freshCandidate.NewVersion == pending.NewVersion
                    && freshCandidate.NewDate == pending.NewDate)
                {
                    revalidated++;
                    _logger.LogDebug(
                        "Cache reconciliation revalidated {Device}: {SourceUpdateId} {Version}",
                        row.DeviceName,
                        freshCandidate.SourceUpdateId,
                        freshCandidate.NewVersion);
                }
                else
                {
                    replaced++;
                    _logger.LogInformation(
                        "Cache reconciliation replaced update for {Device}: old={OldId} {OldVersion} ({OldDate}), new={NewId} {NewVersion} ({NewDate}); the old result will not be saved again",
                        row.DeviceName,
                        pending.SourceUpdateId,
                        pending.NewVersion,
                        pending.NewDate,
                        freshCandidate.SourceUpdateId,
                        freshCandidate.NewVersion,
                        freshCandidate.NewDate);
                }
                continue;
            }

            if (pending is null)
            {
                continue;
            }

            // The proven-ineffective ledger must win here too. Without this check a candidate
            // the current scan deliberately suppressed comes straight back as a cached
            // fallback, gets written to the cache again, and reappears on every later scan -
            // exactly the loop the ledger exists to break.
            var safeCacheFallback = IsSafeCacheFallback(pending);
            if (safeCacheFallback
                && pending.IsNewerThan(row.Driver)
                && !IsProvenIneffective(row, pending))
            {
                // The candidate has just been re-compared against the version this scan read
                // from the machine, so it is a real pending update; only its provenance is
                // older than this run. Keep it offered - sources drop in and out between scans
                // (a scraper that failed, a source disabled in settings, an AI-discovered lead
                // that a plain scan never re-derives), and marking those rows unverifiable left
                // them permanently stuck: the next scan restored them from cache again.
                row.AvailableUpdate = pending;
                row.IsUpdateFromCache = true;
                row.Status = row.IsExcluded ? DriverStatus.Excluded : DriverStatus.Outdated;
                restored++;
                _logger.LogDebug(
                    "Cache reconciliation restored fallback for {Device}: {SourceUpdateId} {Version}",
                    row.DeviceName,
                    pending.SourceUpdateId,
                    pending.NewVersion);
            }
            else
            {
                dropped++;
                if (!safeCacheFallback)
                {
                    _logger.LogInformation(
                        "Cache reconciliation dropped unverified vendor fallback for {Device}: cached={SourceUpdateId}, confidence={Confidence}, kind={InstallKind}; the source must validate it again",
                        row.DeviceName,
                        pending.SourceUpdateId,
                        pending.Confidence,
                        pending.InstallKind);
                }
                else
                {
                    _logger.LogInformation(
                        "Cache reconciliation dropped obsolete update for {Device}: cached={SourceUpdateId} {Version}, installed={InstalledVersion}; the old result will be removed from cache",
                        row.DeviceName,
                        pending.SourceUpdateId,
                        pending.NewVersion,
                        row.Driver.CurrentVersion);
                }
            }
        }

        RefreshUpdateCounts();
        _logger.LogInformation(
            "Cache reconciliation completed: {Fresh} fresh result(s), {Revalidated} revalidated, {Replaced} replaced old cache result(s), {Restored} cached fallback result(s), {Dropped} obsolete result(s) removed",
            fresh,
            revalidated,
            replaced,
            restored,
            dropped);
    }

    private static bool IsSafeCacheFallback(UpdateCandidate candidate) =>
        candidate.Confidence == UpdateConfidence.Confirmed
        && candidate.InstallKind != UpdateInstallKind.VendorPage;

    private async Task<bool> QueryUpdateSourcesAsync(CancellationToken cancellationToken)
    {
        if (Drivers.Count == 0)
        {
            return true;
        }

        _skipAiSearchRequested = false;

        await LoadIneffectiveLedgerAsync(cancellationToken).ConfigureAwait(true);
        if (!await LoadExcludedDriversAsync(cancellationToken).ConfigureAwait(true))
        {
            return false;
        }
        ApplyExclusionsToRows();

        var index = BuildHardwareIdIndex();
        var driverSnapshots = Drivers
            .Select(d => d.Driver)
            .ToArray();

        var settings = _updaterSettings?.CurrentValue;
        foreach (var source in _updateSources)
        {
            if (settings is not null && IsSourceDisabled(source, settings))
            {
                _logger.LogInformation("Skipping {Source}: disabled in settings", source.DisplayName);
                continue;
            }

            var received = 0;
            var accepted = 0;
            try
            {
                StatusText = $"Querying {source.DisplayName}...";
                _logger.LogInformation("Querying {Source}", source.DisplayName);

                await foreach (var candidate in source.SearchAsync(driverSnapshots, cancellationToken))
                {
                    received++;
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
                        row.IsUpdateFromCache = false;
                        // An excluded device keeps the found version on show, but its status
                        // must keep saying the update is not going to be applied.
                        row.Status = row.IsExcluded ? DriverStatus.Excluded : DriverStatus.Outdated;
                        accepted++;
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
            finally
            {
                _logger.LogInformation(
                    "Update source completed: {Source}; candidates received={Received}, accepted into app={Accepted}, filtered={Filtered}",
                    source.DisplayName,
                    received,
                    accepted,
                    received - accepted);
            }
        }

        return true;
    }

    private async Task RunAiPostScanAsync(CancellationToken cancellationToken)
    {
        var rowsReviewedAsCandidates = await VerifyCandidatesWithAiAsync(cancellationToken).ConfigureAwait(true);
        if (_aiVerifier?.IsTemporarilyUnavailable == true)
        {
            _logger.LogInformation("AI post-scan discovery skipped because the provider is temporarily unavailable");
            StatusText = "AI search unavailable. Keeping deterministic scan results.";
            return;
        }
        await DiscoverLatestDriversWithAiAsync(
            onlyRowsWithoutUpdates: true,
            rowsReviewedAsCandidates,
            cancellationToken).ConfigureAwait(true);

    }

    // The devices the user excluded from updating. Read before every scan and whenever the
    // Settings window closes, so a driver ticked there stops being offered without a restart.
    private async Task<bool> LoadExcludedDriversAsync(CancellationToken cancellationToken)
    {
        if (_exclusionStore is null)
        {
            return true;
        }

        try
        {
            var exclusions = await _exclusionStore.LoadAsync(cancellationToken).ConfigureAwait(true);
            _excludedDeviceIds = new HashSet<string>(exclusions.DeviceIds, StringComparer.OrdinalIgnoreCase);
            if (_excludedDeviceIds.Count > 0)
            {
                _logger.LogInformation(
                    "{Count} device(s) are excluded from updating; no update is offered for them",
                    _excludedDeviceIds.Count);
            }
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not load the excluded-driver list; driver updates are blocked");
            StatusText = "Could not read the excluded-driver list. Driver updates are blocked until it is available.";
            return false;
        }
    }

    private bool IsExcluded(DriverInfo driver) => _excludedDeviceIds.Contains(driver.DeviceId);

    // Applies the current exclusion list to the rows already on screen, so a driver ticked in
    // Settings stops being offered right away and one that was unticked is offered again
    // without waiting for the next scan. The found version stays on the row either way: an
    // excluded device still shows what is out there, it just never installs it.
    private void ApplyExclusionsToRows()
    {
        var suppressed = 0;
        foreach (var row in Drivers)
        {
            var excluded = IsExcluded(row.Driver);
            if (row.IsExcluded == excluded)
            {
                continue;
            }

            row.IsExcluded = excluded;
            if (excluded)
            {
                row.Status = DriverStatus.Excluded;
                if (row.AvailableUpdate is not null)
                {
                    suppressed++;
                }
            }
            else if (row.Status == DriverStatus.Excluded)
            {
                row.Status = row.AvailableUpdate is null ? DriverStatus.Unknown : DriverStatus.Outdated;
            }
        }

        if (suppressed > 0)
        {
            _logger.LogInformation(
                "{Count} found update(s) are shown but will not be installed: their device is excluded from updating",
                suppressed);
        }

        RefreshUpdateCounts();
        DriversView.Refresh();
    }

    private static string IneffectiveKey(string deviceId, string targetVersion) => deviceId + "|" + targetVersion;

    // Hardware identity sharpens every recommendation, but a machine that will not answer a WMI
    // query must not cost the user their answer.
    private async Task<MachineProfile?> ReadMachineProfileAsync(CancellationToken cancellationToken)
    {
        if (_machineProfileProvider is null)
        {
            return DetectedOem is { } oem
                ? MachineProfile.Empty with { SystemManufacturer = oem.Manufacturer, SystemModel = oem.Model }
                : null;
        }

        try
        {
            return await _machineProfileProvider.GetAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read the machine profile for the driver chat prompt");
            return null;
        }
    }

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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
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
            .Where(r => r.IsScannedThisRun)
            .Where(r => r.HasAvailableUpdate)
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

        var aiSearchCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _aiSearchCancellation = aiSearchCancellation;
        IsAiSearchRunning = true;
        var stopwatch = Stopwatch.StartNew();
        IReadOnlyDictionary<string, AiVerdict> verdicts;
        try
        {
            StatusText = $"Verifying existing updates with AI... 1-{targets.Length} of {Drivers.Count}";
            verdicts = await _aiVerifier.VerifyAsync(requests, unattendedRun: false, aiSearchCancellation.Token).ConfigureAwait(true);
            stopwatch.Stop();
        }
        catch (OperationCanceledException) when (
            aiSearchCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation(
                "AI verification skipped by the user after {ElapsedMs} ms",
                stopwatch.ElapsedMilliseconds);
            StatusText = "AI search skipped. Continuing the scan...";
            return reviewedRows;
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
        finally
        {
            if (ReferenceEquals(_aiSearchCancellation, aiSearchCancellation))
            {
                _aiSearchCancellation = null;
                IsAiSearchRunning = false;
            }
            aiSearchCancellation.Dispose();
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
        if (!row.IsScannedThisRun)
        {
            StatusText = "Run Scan before asking AI to review this driver.";
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
            var verdicts = await _aiVerifier.VerifyAsync(new[] { request }, unattendedRun: false, cancellationToken).ConfigureAwait(true);
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
                    ? $"AI found a newer driver for {row.DeviceName}: {row.AvailableVersionText}. Run the update to continue."
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
        if (_skipAiSearchRequested)
        {
            _logger.LogInformation("AI latest-driver discovery skipped because the user skipped AI search");
            StatusText = "AI search skipped. Continuing the scan...";
            return;
        }
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
            .Where(r => r.IsScannedThisRun)
            .Where(r => !r.IsExcluded)
            .Where(r => !alreadyReviewed.Contains(r))
            .Where(r => !onlyRowsWithoutUpdates || !r.HasAvailableUpdate)
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
        var providerUnavailable = false;
        var aiSearchCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _aiSearchCancellation = aiSearchCancellation;
        IsAiSearchRunning = true;
        var discoveryCancellationToken = aiSearchCancellation.Token;

        var batches = targets.Chunk(AiDiscoveryBatchSize).ToArray();
        var stopDispatching = false;
        var userCancelled = false;
        using var inFlight = new SemaphoreSlim(AiDiscoveryParallelism);

        // The batches are independent requests with identical content either way, so running a few
        // at a time only removes the waiting between them. A scan of 100 drivers used to be five
        // round trips end to end.
        async Task RunDiscoveryBatchAsync(DriverRowViewModel[] batch)
        {
            await inFlight.WaitAsync(discoveryCancellationToken).ConfigureAwait(true);
            var started = false;
            try
            {
                if (stopDispatching || _skipAiSearchRequested)
                {
                    return;
                }
                discoveryCancellationToken.ThrowIfCancellationRequested();

                started = true;
                foreach (var row in batch)
                {
                    row.IsAiChecking = true;
                }

                var requests = batch.Select(BuildAiDiscoveryRequest).ToArray();
                StatusText =
                    $"Asking AI to find latest drivers... {processed} of {targets.Length} done. Waiting for AI response...";
                _logger.LogInformation(
                    "AI latest-driver discovery: provider={Provider}, sending a batch of {BatchSize} row(s), {Processed} of {DiscoveryTotal} discovery rows done, {TotalDrivers} drivers in total",
                    _aiVerifier.Provider,
                    batch.Length,
                    processed,
                    targets.Length,
                    Drivers.Count);
                foreach (var request in requests)
                {
                    LogAiRequest("latest-driver discovery", request);
                }

                var verdicts = await _aiVerifier.VerifyAsync(requests, unattendedRun: false, discoveryCancellationToken).ConfigureAwait(true);
                if (_aiVerifier.IsTemporarilyUnavailable)
                {
                    providerUnavailable = true;
                    stopDispatching = true;
                    failedBatches++;
                    withoutVerdict += batch.Length;
                    _logger.LogWarning(
                        "AI latest-driver discovery stopped because provider {Provider} became temporarily unavailable",
                        _aiVerifier.Provider);
                    StatusText = "AI search unavailable. Keeping deterministic scan results.";
                    return;
                }
                if (_skipAiSearchRequested)
                {
                    stopDispatching = true;
                    StatusText = "AI search skipped. Continuing the scan...";
                    return;
                }
                if (verdicts.Count == 0)
                {
                    failedBatches++;
                    _logger.LogWarning(
                        "AI latest-driver discovery returned no usable results for a batch of {BatchSize} row(s); continuing with the remaining batches",
                        batch.Length);
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
            catch (OperationCanceledException) when (
                _skipAiSearchRequested && !cancellationToken.IsCancellationRequested)
            {
                stopDispatching = true;
                userCancelled = true;
                _logger.LogInformation("AI latest-driver discovery skipped by the user");
                StatusText = "AI search skipped. Continuing the scan...";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failedBatches++;
                withoutVerdict += batch.Length;
                _logger.LogWarning(ex, "AI latest-driver discovery batch failed");
            }
            finally
            {
                if (started)
                {
                    foreach (var row in batch)
                    {
                        row.IsAiChecking = false;
                    }
                    processed += batch.Length;
                }
                inFlight.Release();
            }
        }

        var batchTasks = new List<Task>(batches.Length);
        foreach (var batch in batches)
        {
            batchTasks.Add(RunDiscoveryBatchAsync(batch));
        }

        try
        {
            await Task.WhenAll(batchTasks).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (userCancelled || (_skipAiSearchRequested && !cancellationToken.IsCancellationRequested))
        {
            StatusText = "AI search skipped. Continuing the scan...";
        }

        if (ReferenceEquals(_aiSearchCancellation, aiSearchCancellation))
        {
            _aiSearchCancellation = null;
            IsAiSearchRunning = false;
        }
        aiSearchCancellation.Dispose();

        if (!providerUnavailable)
        {
            StatusText =
                $"AI latest-driver search complete. {alreadyReviewed.Count + processed} of {Drivers.Count} drivers processed, {found} possible updates found, {noNewer} already current, {withoutVerdict} no result."
                + (failedBatches > 0 ? $" {failedBatches} batch(es) failed." : string.Empty);
        }
        _logger.LogInformation(
            "AI latest-driver discovery complete: reviewedCandidates={ReviewedCandidates}, discoveryTargets={Targets}, totalDrivers={TotalDrivers}, found={Found}, noNewer={NoNewer}, withoutVerdict={WithoutVerdict}, failedBatches={FailedBatches}",
            alreadyReviewed.Count, targets.Length, Drivers.Count, found, noNewer, withoutVerdict, failedBatches);
    }

    private bool CanSkipAiSearch() => IsAiSearchRunning && _aiSearchCancellation is not null;

    [RelayCommand(CanExecute = nameof(CanSkipAiSearch))]
    private void SkipAiSearch()
    {
        if (_aiSearchCancellation is null)
        {
            return;
        }

        _skipAiSearchRequested = true;
        _logger.LogInformation("AI search skipped by the user");
        StatusText = "Skipping AI search...";
        _aiSearchCancellation.Cancel();
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
            CandidateVersion: row.AvailableUpdate.DisplayVersion,
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
        if (IsAmdChipsetBundleLead(row, verdict, url))
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
        row.IsUpdateFromCache = false;
        row.Status = DriverStatus.Outdated;
        return true;
    }

    private static bool IsAmdChipsetBundleLead(
        DriverRowViewModel row,
        AiVerdict verdict,
        Uri url)
    {
        if (!AmdChipsetSource.IsSupportedAmdChipsetDriver(row.Driver))
        {
            return false;
        }

        if (url.AbsolutePath.Contains("/chipsets/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var evidence = string.Join(' ',
            verdict.Rationale,
            verdict.CandidateSuitability,
            verdict.AdvisorNote,
            verdict.Summary);
        return evidence.Contains("chipset driver", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("chipset package", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("chipset software", StringComparison.OrdinalIgnoreCase);
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
            row.IsUpdateFromCache = false;
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
        row.IsUpdateFromCache = false;
        row.Status = DriverStatus.Outdated;
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
        Uri.TryCreate(raw, UriKind.Absolute, out var uri)
        && (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            ? uri
            : null;

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
        UpdatesFoundCount = Drivers.Count(d => d.HasAvailableUpdate);
        ExcludedUpdatesFoundCount = Drivers.Count(d => d.IsExcluded && d.AvailableUpdate is not null);
        ConfirmedUpdatesCount = Drivers.Count(d =>
            d.HasAvailableUpdate && d.AvailableUpdate?.Confidence == UpdateConfidence.Confirmed);
        VendorChecksCount = Drivers.Count(d =>
            d.HasAvailableUpdate && d.AvailableUpdate?.Confidence == UpdateConfidence.Advisory);
    }

    // Every source that points at a vendor page produces a lead, not an installer. Turning
    // those into a real package
    // here, while the scan is running, is what makes the offer honest: a row that survives
    // this pass can be installed from inside the app, and a row that does not is removed.
    private async Task ResolveVendorPageCandidatesAsync(CancellationToken cancellationToken)
    {
        if (_vendorPageResolver is null)
        {
            return;
        }

        var pending = Drivers
            .Where(row => row.AvailableUpdate is { InstallKind: UpdateInstallKind.VendorPage })
            .GroupBy(row => row.AvailableUpdate!.DownloadUrl)
            .ToArray();
        if (pending.Length == 0)
        {
            return;
        }

        _logger.LogInformation(
            "Resolving {PageCount} vendor page(s) into installable packages for {RowCount} row(s)",
            pending.Length,
            pending.Sum(group => group.Count()));

        var resolvedPages = 0;
        var droppedPages = 0;
        var advisoryRows = 0;
        var index = 0;
        foreach (var group in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            index++;
            StatusText = $"Checking vendor downloads... {index} of {pending.Length}";

            var resolution = VendorPageResolution.NoPackageFound;
            try
            {
                resolution = await _vendorPageResolver
                    .TryResolveAsync(group.First().AvailableUpdate!, cancellationToken)
                    .ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Vendor page resolve threw for {Url}", group.Key);
            }

            foreach (var row in group)
            {
                switch (resolution.Kind)
                {
                    case VendorPageResolutionKind.Installer:
                        var original = row.AvailableUpdate!;
                        var resolved = resolution.Candidate!;
                        var installerKind = resolved.SourceUpdateId
                            .Split(':', StringSplitOptions.RemoveEmptyEntries)
                            .Skip(1)
                            .FirstOrDefault() ?? string.Empty;
                        if (!VendorPageInstallerResolver.IsPackageCompatibleWithHardware(
                                original,
                                resolved.DownloadUrl,
                                installerKind))
                        {
                            _logger.LogWarning(
                                "Vendor package {Package} resolved from {Page} was rejected for {Device} ({HardwareId}): installer family {InstallerKind} does not match the device",
                                resolved.DownloadUrl,
                                group.Key,
                                row.DeviceName,
                                row.HardwareId,
                                installerKind);
                            if (original.AiVerification is not null)
                            {
                                row.Status = DriverStatus.ManualActionRequired;
                                advisoryRows++;
                            }
                            else
                            {
                                row.AvailableUpdate = null;
                                row.Status = DriverStatus.NotFound;
                            }
                            break;
                        }

                        // One page may serve multiple rows. Keep each row's device binding and
                        // version, but only after the resolved package family matches that row.
                        row.AvailableUpdate = original with
                        {
                            DownloadUrl = resolved.DownloadUrl,
                            InstallKind = resolved.InstallKind,
                            Confidence = resolved.Confidence,
                            SourceUpdateId = resolved.SourceUpdateId
                        };
                        row.Status = DriverStatus.Outdated;
                        _logger.LogInformation(
                            "Vendor update confirmed for {Device} ({HardwareId}): package={Package}, kind={InstallerKind}, target={Version}",
                            row.DeviceName,
                            row.HardwareId,
                            resolved.DownloadUrl,
                            installerKind,
                            row.AvailableUpdate.NewVersion);
                        break;
                    case VendorPageResolutionKind.NoPackageFound:
                    case VendorPageResolutionKind.PageUnreachable:
                        // A page URL is not an update package, so the app never claims it can
                        // install one. A lead that carries an AI verdict still carries the
                        // finding itself - which version is current and how to get it - so the
                        // row stays as an advisory the user can read instead of vanishing from
                        // a finished scan. A bare page lead has nothing left to show and goes.
                        if (row.AvailableUpdate?.AiVerification is not null)
                        {
                            row.Status = DriverStatus.ManualActionRequired;
                            advisoryRows++;
                        }
                        else
                        {
                            row.AvailableUpdate = null;
                            row.Status = DriverStatus.NotFound;
                        }
                        break;
                }
            }

            switch (resolution.Kind)
            {
                case VendorPageResolutionKind.Installer: resolvedPages++; break;
                case VendorPageResolutionKind.NoPackageFound:
                case VendorPageResolutionKind.PageUnreachable: droppedPages++; break;
            }
        }

        RefreshUpdateCounts();
        _logger.LogInformation(
            "Vendor page resolution complete: {Resolved} page(s) resolved to an in-app installer, " +
            "{Dropped} page(s) had no validated installable package ({Advisory} row(s) kept as an " +
            "AI advisory, the rest dropped)",
            resolvedPages,
            droppedPages,
            advisoryRows);
    }

    private void FinalizeScanStatuses()
    {
        foreach (var row in Drivers.Where(d => d.IsScannedThisRun && d.AvailableUpdate is null && !d.IsExcluded))
        {
            if (row.Status == DriverStatus.Unknown)
            {
                row.Status = DriverStatus.NotFound;
            }
        }
    }

    private static bool ShouldAcceptCandidate(DriverRowViewModel row, UpdateCandidate candidate) =>
        DriverUpdateMatcher.ShouldReplace(row.AvailableUpdate, candidate);

    private bool CanScan() => !IsScanning && !IsUpdatingWithAi;

    private bool CanCancelScan() => IsScanning && _scanCancellation is not null;

    [RelayCommand]
    private void OpenHistory()
    {
        _historyWindowOpener.Open();
    }

    [RelayCommand]
    private async Task OpenSettingsAsync(CancellationToken cancellationToken)
    {
        // Modal, so this returns once the user is done. Re-reading the exclusion list here is
        // what makes a driver ticked in Settings disappear from the grid straight away.
        _settingsWindowOpener.Open();
        if (!await LoadExcludedDriversAsync(cancellationToken).ConfigureAwait(true))
        {
            return;
        }
        ApplyExclusionsToRows();
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
            .Where(r => r.HasAvailableUpdate
                && r.AvailableUpdate is { InstallKind: UpdateInstallKind.VendorPage })
            .ToArray();

        if (pageTargets.Length == 0)
        {
            StatusText = "No pending updates to resolve.";
            return;
        }

        await RunUpdatesAsync(pageTargets, dryRun: false, includeVendorPages: true, cancellationToken).ConfigureAwait(true);
    }

    private bool CanUpdateAll() => !IsUpdatingWithAi && Drivers.Any(r => r.CanUpdate);

    private bool CanOpenVendorChecks() => VendorChecksCount > 0;

    // ----- Driver version history and downgrade -----

    [RelayCommand]
    private void ShowVersionHistory(DriverRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }
        if (_versionHistoryWindowOpener is null || _downgradeService is null)
        {
            StatusText = "Version history is not available in this build.";
            return;
        }
        _versionHistoryWindowOpener.Open(row.Driver, target => DowngradeRowAsync(row, target));
    }

    private async Task<bool> DowngradeRowAsync(DriverRowViewModel row, DriverVersionRecord target)
    {
        if (_downgradeService is null)
        {
            return false;
        }

        var displayName = DriverDisplayName(row);
        StatusText = $"Restoring {displayName} to version {target.Version}...";
        _logger.LogInformation(
            "Downgrade requested: {Device} from {Current} to {Target}",
            displayName, row.Driver.CurrentVersion, target.Version);

        if (_restorePointService is not null)
        {
            var restorePoint = await _restorePointService
                .CreateRestorePointAsync($"DriverUpdater downgrade: {displayName} {target.Version}")
                .ConfigureAwait(true);
            if (!restorePoint.IsSuccess)
            {
                _logger.LogWarning(
                    "Downgrade: could not create a restore point for {Device}: {Error}",
                    displayName, restorePoint.Error.Message);
            }
        }

        var result = await _downgradeService.DowngradeAsync(row.Driver, target).ConfigureAwait(true);
        if (!result.IsSuccess)
        {
            StatusText = $"Downgrade failed: {result.Error.Message}";
            _logger.LogWarning("Downgrade failed for {Device}: {Error}", displayName, result.Error.Message);
            return false;
        }

        var outcome = result.Value;
        if (!outcome.VerifiedDowngraded)
        {
            StatusText = $"Downgrade finished, but {displayName} reports version "
                + $"{outcome.BoundVersionAfter ?? "(unknown)"}. A restart may be required before it applies.";
            row.Status = DriverStatus.RestartRequired;
            _logger.LogWarning(
                "Downgrade: {Device} still reports {Bound} instead of {Target}; deferring to a restart",
                displayName, outcome.BoundVersionAfter ?? "(unknown)", target.Version);
            return false;
        }

        // The row keeps rendering the pre-downgrade DriverInfo until the next scan; the status
        // and cleared candidate make the outcome visible immediately.
        row.AvailableUpdate = null;
        row.Status = DriverStatus.NotUpdated;
        RefreshUpdateCounts();
        await ExcludeDeviceAfterDowngradeAsync(row).ConfigureAwait(true);
        StatusText = $"{displayName} is back on version {target.Version}. "
            + "The device was added to the never-update list so the newer driver is not offered again.";
        return true;
    }

    // Without this, the very next scan would flag the freshly restored driver as outdated and
    // an Update All would immediately undo the user's decision.
    private async Task ExcludeDeviceAfterDowngradeAsync(DriverRowViewModel row)
    {
        if (_exclusionStore is null || _excludedDeviceIds.Contains(row.Driver.DeviceId))
        {
            return;
        }
        try
        {
            var exclusions = await _exclusionStore.LoadAsync().ConfigureAwait(true);
            if (!exclusions.Contains(row.Driver.DeviceId))
            {
                await _exclusionStore
                    .SaveAsync(new DriverUpdateExclusions(
                        exclusions.DeviceIds.Append(row.Driver.DeviceId).ToArray()))
                    .ConfigureAwait(true);
            }
            _excludedDeviceIds.Add(row.Driver.DeviceId);
            _logger.LogInformation(
                "Downgrade: {Device} was excluded from future updates to keep the restored version",
                DriverDisplayName(row));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "Downgrade: could not persist the update exclusion for {Device}", DriverDisplayName(row));
        }
    }

    public event EventHandler<DriverRowViewModel>? ScrollToRowRequested;

    private async Task RunUpdatesAsync(
        IEnumerable<DriverRowViewModel> requested,
        bool dryRun,
        bool includeVendorPages,
        CancellationToken cancellationToken)
    {
        // Track the live run so the driver chat can tell the AI that the log tail reflects an
        // update that is still in progress (e.g. "why is the update taking so long?").
        _activeUpdateRuns++;
        try
        {
            await RunUpdatesCoreAsync(requested, dryRun, includeVendorPages, cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            _activeUpdateRuns--;
        }
    }

    private async Task RunUpdatesCoreAsync(
        IEnumerable<DriverRowViewModel> requested,
        bool dryRun,
        bool includeVendorPages,
        CancellationToken cancellationToken)
    {
        var targets = requested
            .Where(r => r.CanUpdate)
            .ToArray();

        if (targets.Length == 0)
        {
            StatusText = "No outdated drivers to update.";
            return;
        }

        // Selection is by install kind only. Every row here already passed CanUpdate, so
        // re-filtering on DriverStatus would only drop rows whose button the user just
        // clicked - the retry after a failed install, the result carried over from the last
        // scan - and turn that click into a silent no-op.
        var installTargets = targets
            .Where(r => r.AvailableUpdate is { InstallKind: UpdateInstallKind.WindowsUpdate or UpdateInstallKind.PnPUtilPackage or UpdateInstallKind.VendorInstaller })
            .ToArray();
        var pageTargets = targets
            .Where(r => r.AvailableUpdate is { InstallKind: UpdateInstallKind.VendorPage })
            .ToArray();

        // Vendor page rows go through the pipeline too: it tries to resolve a direct
        // installer from the page and install silently. If that fails, the row remains
        // unresolved in the application and no external browser is launched.
        if (!dryRun && includeVendorPages && pageTargets.Length > 0)
        {
            installTargets = installTargets.Concat(pageTargets).ToArray();
        }

        if (installTargets.Length == 0)
        {
            StatusText = dryRun
                ? $"Dry run completed. {pageTargets.Length} updates require in-app installer resolution."
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
        var unresolvedVendorChecks = new List<DriverRowViewModel>();
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

            var originalCandidate = row.AvailableUpdate;
            var originalUpdateId = originalCandidate.SourceUpdateId;
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
                // Progress<T> marshals through the dispatcher, so a report can still be queued
                // when this method resumes. Applying a terminal report here would overwrite the
                // final row state decided below. The block after the await owns the terminal
                // state.
                if (report.IsTerminal)
                {
                    return;
                }
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
                outcomes.AddRange(ApplySharedVendorInstallerResult(finished, row, originalCandidate));
            }

            if (finished.Status == UpdateStatus.Skipped
                && finished.Candidate.InstallKind == UpdateInstallKind.VendorPage)
            {
                row.Status = DriverStatus.ManualActionRequired;
                if (!dryRun)
                {
                    _logger.LogInformation(
                        "Update run: {Device} could not be updated in-app from its vendor page ({Url}); " +
                        "no browser will be opened. Reason: {Reason}. See the 'Vendor page resolve' log lines above " +
                        "for the links found on the page and why none were directly installable.",
                        displayName, finished.Candidate.DownloadUrl,
                        string.IsNullOrWhiteSpace(finished.ErrorMessage) ? "no direct installer found on the page" : finished.ErrorMessage);
                    unresolvedVendorChecks.Add(row);
                }
            }
        }

        LogRunSummary(runStartedAt, dryRun, unresolvedVendorChecks, installTargets, installAttemptCount, outcomes, skipped);

        StatusText = dryRun
            ? $"Dry run completed for {installTargets.Length} drivers."
            : unresolvedVendorChecks.Count > 0
                ? $"Install completed for {installTargets.Length} drivers. {unresolvedVendorChecks.Count} updates had no safe in-app installer."
                : includeVendorPages
                    ? $"Install completed for {installTargets.Length} drivers."
                    : $"Install completed for {installTargets.Length} confirmed drivers.";

        if (!dryRun)
        {
            if (_postUpdateSummaryCoordinator is not null)
            {
                StatusText = "Verifying installed drivers and preparing the summary...";
                var report = await _postUpdateSummaryCoordinator.CompleteRunAsync(
                    outcomes.Select(o => o.Operation).ToArray(),
                    report => ApplyPostUpdateVerification(report, outcomes),
                    cancellationToken).ConfigureAwait(true);
                if (report is null && outcomes.Any(outcome => outcome.Operation.RequiresRestart))
                {
                    foreach (var outcome in outcomes.Where(outcome => outcome.Operation.RequiresRestart))
                    {
                        outcome.Row.Status = DriverStatus.RestartRequired;
                    }
                    RefreshUpdateCounts();
                    StatusText = "Restart required. The complete update summary will appear after restart.";
                }
                else
                {
                    StatusText = "Driver updates checked. Review the summary for the final result.";
                }
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
            o.Operation.RequiresRestart);
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
        IReadOnlyList<DriverRowViewModel> unresolvedVendorChecks,
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
            .Append(", unresolved vendor checks ").Append(unresolvedVendorChecks.Count)
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
                // Prefer the version read back from Windows after the install. The candidate's
                // target is what the source advertised, which for date-stamped or differently
                // branded vendor releases never matches the driver version Windows now reports.
                var installed = op.VerifiedState?.Version?.ToString()
                    ?? op.VerifiedState?.Date?.ToString();
                var target = op.Candidate.DisplayVersion;
                sb.Append("    - ").Append(row.DeviceName)
                    .Append(" [").Append(row.HardwareId).Append(']')
                    .Append(": ").Append(op.TargetSnapshot.CurrentVersion?.ToString() ?? "?")
                    .Append(" → ").Append(installed ?? target)
                    .Append(installed is null || string.Equals(installed, target, StringComparison.OrdinalIgnoreCase)
                        ? string.Empty
                        : $" (package {target})")
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
        var withUpdates = Drivers.Where(d => d.HasAvailableUpdate).ToArray();
        var carriedOver = withUpdates.Count(d => d.IsUpdateFromCache);
        var noUpdateCount = Drivers.Count - withUpdates.Length;

        var sb = new System.Text.StringBuilder();
        sb.Append("Scan result summary: ").Append(Drivers.Count).Append(" total drivers, ")
            .Append(withUpdates.Length).Append(" with an available update (")
            .Append(withUpdates.Length - carriedOver).Append(" found by a source this run, ")
            .Append(carriedOver).Append(" carried over from the last scan), ")
            .Append(noUpdateCount).AppendLine(" up-to-date / no update found");

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
                    .Append(", available=").Append(row.AvailableUpdate!.DisplayVersion)
                    .Append(", source=").Append(row.AvailableUpdate.Source)
                    .AppendLine();
            }
        }

        _logger.LogInformation("{Summary}", sb.ToString().TrimEnd());
    }

    private IReadOnlyList<(DriverRowViewModel Row, UpdateOperation Operation)> ApplySharedVendorInstallerResult(
        UpdateOperation finished,
        DriverRowViewModel masterRow,
        UpdateCandidate originalCandidate)
    {
        // Every row that shares the SourceUpdateId is really the same install (think 18
        // AMD chipset device rows that all point at amd_chipset_software_X.Y.Z.exe). Once
        // the master row finishes, those duplicate rows have already been touched in the
        // same way and should disappear from the grid: keeping them in the Installable
        // filter makes it look like there is still work pending when there is not.
        // The master row keeps its AvailableUpdate on failure so the user can retry it
        // explicitly without having to rescan.
        // A vendor page candidate that was resolved to a direct AMD chipset installer finishes
        // with a per-row SourceUpdateId. Sibling AMD components still point at the same original
        // chipset page, so treat them as one shared package and verify each component afterward.
        var isResolvedAmdChipsetPackage =
            finished.Candidate.SourceUpdateId.StartsWith(
                "vendor-installer:amd-chipset:",
                StringComparison.OrdinalIgnoreCase)
            && originalCandidate.InstallKind == UpdateInstallKind.VendorPage;
        var sharedOutcomes = new List<(DriverRowViewModel Row, UpdateOperation Operation)>();
        foreach (var row in Drivers.Where(r =>
            r.AvailableUpdate?.SourceUpdateId is { } id
            && (string.Equals(id, finished.Candidate.SourceUpdateId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(id, originalCandidate.SourceUpdateId, StringComparison.OrdinalIgnoreCase)
                || (isResolvedAmdChipsetPackage
                    && r.AvailableUpdate.InstallKind == UpdateInstallKind.VendorPage
                    && r.AvailableUpdate.DownloadUrl == originalCandidate.DownloadUrl))))
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
                        VersionLabel = row.AvailableUpdate.VersionLabel,
                        InstalledVersionLabel = row.AvailableUpdate.InstalledVersionLabel,
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
                StatusText = $"{oem.ToolName} is not installed. DriverUpdater will not open an external download page.";
                _logger.LogInformation(
                    "OEM tool {Tool} is not installed; external support page {Url} was not opened",
                    oem.ToolName,
                    oem.FallbackUrl);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not open OEM tool or URL");
            StatusText = $"Could not open {oem.ToolName}: {ex.Message}";
        }
    }

    private bool CanOpenOemTool() => DetectedOem is { ToolInstalled: true, ToolPath: not null };

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
        // An excluded device with a found version belongs under "Updates available": the
        // filter is about what a scan turned up, not about what the app is going to install.
        DriverUpdateFilter.UpdatesAvailable => row.ShowsUpdateInfo,
        DriverUpdateFilter.NoUpdateAvailable => !row.ShowsUpdateInfo,
        DriverUpdateFilter.ExcludedDrivers => row.IsExcluded,
        DriverUpdateFilter.ExcludedWithUpdates => row.HasSuppressedUpdate,
        _ => true
    };
}
