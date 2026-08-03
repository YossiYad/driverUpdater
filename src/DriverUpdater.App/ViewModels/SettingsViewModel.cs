using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DriverUpdater.App.Services;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using DriverUpdater.Core.Options;
using Microsoft.Extensions.Logging;

namespace DriverUpdater.App.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsStore _settingsStore;
    private readonly ISchedulerService _schedulerService;
    private readonly ILocalizationService? _localizationService;
    private readonly IAppUpdater? _appUpdater;
    private readonly IAppUpdatePrompt? _appUpdatePrompt;
    private readonly ILogCleanupService? _logCleanupService;
    private readonly IDriverCacheStore? _driverCacheStore;
    private readonly ApplicationBehaviorState? _applicationBehaviorState;
    private readonly IApplicationStartupService? _applicationStartupService;
    private readonly IAutoUpdateSelectionStore? _autoUpdateSelectionStore;
    private readonly IAutoUpdateSelectionWindowOpener? _autoUpdateSelectionWindowOpener;
    private readonly ILogger<SettingsViewModel> _logger;

    public IReadOnlyList<ScheduleCadence> AvailableCadences { get; } = Enum.GetValues<ScheduleCadence>().ToArray();
    public IReadOnlyList<DayOfWeek> AvailableDays { get; } = Enum.GetValues<DayOfWeek>().ToArray();
    public IReadOnlyList<AppLanguage> AvailableLanguages { get; } = Enum.GetValues<AppLanguage>().ToArray();
    public IReadOnlyList<AppLanguage> AvailableAiResponseLanguages { get; } =
        [AppLanguage.English, AppLanguage.Hebrew];
    public IReadOnlyList<AiProvider> AvailableAiProviders { get; } = Enum.GetValues<AiProvider>().ToArray();

    [ObservableProperty] private AppLanguage _selectedLanguage = AppLanguage.SystemDefault;
    [ObservableProperty] private AppLanguage _selectedAiResponseLanguage = AppLanguage.English;

    /// <summary>Backs the Hebrew/English toggle switch; the settings model still stores an AppLanguage.</summary>
    public bool IsAiResponseLanguageHebrew
    {
        get => SelectedAiResponseLanguage == AppLanguage.Hebrew;
        set => SelectedAiResponseLanguage = value ? AppLanguage.Hebrew : AppLanguage.English;
    }

    partial void OnSelectedAiResponseLanguageChanged(AppLanguage value) =>
        OnPropertyChanged(nameof(IsAiResponseLanguageHebrew));

    [ObservableProperty] private bool _enableWindowsUpdate = true;
    [ObservableProperty] private bool _enableMicrosoftCatalog = true;
    [ObservableProperty] private bool _enableOemHints = true;

    [ObservableProperty] private bool _createRestorePoint = true;
    [ObservableProperty] private bool _backupBeforeInstall = true;
    [ObservableProperty] private int _backupRetentionDays = 30;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowScheduleTiming))]
    private ScheduleMode _scheduleMode = ScheduleMode.Manual;

    [ObservableProperty] private ScheduleCadence _scheduleCadence = ScheduleCadence.Weekly;
    [ObservableProperty] private TimeOnly _scheduleTimeOfDay = new(9, 0);
    [ObservableProperty] private DayOfWeek _scheduleDayOfWeek = DayOfWeek.Monday;
    [ObservableProperty] private bool _acceptedAutoUpdateRisk;

    [ObservableProperty]
    private AutoUpdateScope _autoUpdateScope = AutoUpdateScope.AllDrivers;

    [ObservableProperty] private AiAutoUpdateRiskTolerance _aiRiskTolerance = AiAutoUpdateRiskTolerance.SafeOnly;

    [ObservableProperty] private bool _enablePlaywrightFallback;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStartMinimized))]
    private WindowCloseBehavior _closeBehavior = WindowCloseBehavior.KeepRunningInBackground;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStartMinimized))]
    private bool _startWithWindows;

    [ObservableProperty] private bool _startMinimized;

    [ObservableProperty] private bool _showGuideOnStartup = true;

    [ObservableProperty] private bool _enableAutomaticLogCleanup = true;
    [ObservableProperty] private int _logRetentionDays = LogCleanupSettings.DefaultRetentionDays;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGeminiSelected))]
    [NotifyPropertyChangedFor(nameof(IsOllamaSelected))]
    [NotifyPropertyChangedFor(nameof(ShowAiProviderMissingWarning))]
    private AiProvider _selectedAiProvider = AiProvider.Off;
    public ObservableCollection<GeminiApiKeyEntryViewModel> GeminiApiKeys { get; } =
        new() { new GeminiApiKeyEntryViewModel() };

    public string GeminiApiKey
    {
        get => GeminiApiKeys.FirstOrDefault()?.Value ?? string.Empty;
        set
        {
            if (GeminiApiKeys.Count == 0)
            {
                GeminiApiKeys.Add(new GeminiApiKeyEntryViewModel());
            }

            if (GeminiApiKeys[0].Value == value)
            {
                return;
            }

            GeminiApiKeys[0].Value = value;
            OnPropertyChanged();
        }
    }
    [ObservableProperty] private string _geminiModel = new AiSettings().GeminiModel;
    [ObservableProperty] private bool _enableAiWebSearch = true;
    [ObservableProperty] private int _geminiDailyRequestLimit;
    [ObservableProperty] private bool _showAiScanUsageWarning = true;
    [ObservableProperty] private string _ollamaBaseUrl = "http://localhost:11434";
    [ObservableProperty] private string _ollamaModel = "llama3.1";

    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private bool _isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckForUpdatesCommand))]
    private bool _isCheckingForUpdates;

    /// <summary>Automatically check GitHub for a newer app version on startup.</summary>
    [ObservableProperty] private bool _checkForUpdatesOnStartup;

    /// <summary>When checking on startup, download and install a found update without asking.</summary>
    [ObservableProperty] private bool _autoInstallAppUpdates;

    // Preserved across save so the app-update feed/repo settings are not wiped by the UI.
    private CatalogSettings _loadedCatalog = new();
    private UpdaterSettings _loadedUpdater = new();
    private OnboardingSettings _loadedOnboarding = new();
    private BackupSettings _loadedBackup = new();
    private HistorySettings _loadedHistory = new();

    /// <summary>True when app self-updating is wired up (Velopack), so the button is worth showing.</summary>
    public bool CanCheckForUpdates => _appUpdater is not null;

    public string SettingsPath => _settingsStore.SettingsPath;

    public string LogDirectoryPath =>
        _logCleanupService?.LogDirectory ?? LogCleanupService.DefaultLogDirectory();

    public bool ShowAutoUpdateWarning => ScheduleMode == ScheduleMode.ScanAndUpdate;

    /// <summary>The cadence, day, and time only matter once something is scheduled at all.</summary>
    public bool ShowScheduleTiming => ScheduleMode != ScheduleMode.Manual;

    // ----- The five schedule types the Schedule tab offers -----
    // Each one is a (ScheduleMode, AutoUpdateScope) pair behind the scenes; the tab shows them
    // as one plain-language radio list so nobody has to work out which enum combination means
    // "let the AI update my drivers".

    public bool IsScheduleOff
    {
        get => ScheduleMode == ScheduleMode.Manual;
        set => SetScheduleType(value, ScheduleMode.Manual, AutoUpdateScope);
    }

    public bool IsScanOnlySchedule
    {
        get => ScheduleMode == ScheduleMode.ScanOnly;
        set => SetScheduleType(value, ScheduleMode.ScanOnly, AutoUpdateScope);
    }

    public bool IsGeneralSchedule
    {
        get => ScheduleMode == ScheduleMode.ScanAndUpdate && AutoUpdateScope == AutoUpdateScope.AllDrivers;
        set => SetScheduleType(value, ScheduleMode.ScanAndUpdate, AutoUpdateScope.AllDrivers);
    }

    public bool IsCustomSchedule
    {
        get => ScheduleMode == ScheduleMode.ScanAndUpdate && AutoUpdateScope == AutoUpdateScope.SelectedDrivers;
        set => SetScheduleType(value, ScheduleMode.ScanAndUpdate, AutoUpdateScope.SelectedDrivers);
    }

    public bool IsAiSchedule
    {
        get => ScheduleMode == ScheduleMode.ScanAndUpdate && AutoUpdateScope == AutoUpdateScope.AiRecommended;
        set => SetScheduleType(value, ScheduleMode.ScanAndUpdate, AutoUpdateScope.AiRecommended);
    }

    public bool ShowAiAutoUpdateOptions => IsAiSchedule;

    public bool AiInstallsSafeOnly
    {
        get => AiRiskTolerance == AiAutoUpdateRiskTolerance.SafeOnly;
        set => SetRiskTolerance(value, AiAutoUpdateRiskTolerance.SafeOnly);
    }

    public bool AiInstallsSafeAndCaution
    {
        get => AiRiskTolerance == AiAutoUpdateRiskTolerance.SafeAndCaution;
        set => SetRiskTolerance(value, AiAutoUpdateRiskTolerance.SafeAndCaution);
    }

    /// <summary>How many drivers the custom schedule is allowed to update, for the picker button.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedDriverCountText))]
    private int _selectedDriverCount;

    public string SelectedDriverCountText => SelectedDriverCount switch
    {
        0 => "No driver picked yet. The scheduled run would install nothing.",
        1 => "1 driver is updated automatically.",
        _ => $"{SelectedDriverCount} drivers are updated automatically."
    };

    // WPF clears the previously checked radio before setting the new one, so only a check
    // changes anything here. An uncheck must leave the current type alone.
    private void SetScheduleType(bool isChecked, ScheduleMode mode, AutoUpdateScope scope)
    {
        if (!isChecked)
        {
            return;
        }

        AutoUpdateScope = scope;
        ScheduleMode = mode;
    }

    private void SetRiskTolerance(bool isChecked, AiAutoUpdateRiskTolerance tolerance)
    {
        if (isChecked)
        {
            AiRiskTolerance = tolerance;
        }
    }

    private void NotifyScheduleTypeChanged()
    {
        OnPropertyChanged(nameof(IsScheduleOff));
        OnPropertyChanged(nameof(IsScanOnlySchedule));
        OnPropertyChanged(nameof(IsGeneralSchedule));
        OnPropertyChanged(nameof(IsCustomSchedule));
        OnPropertyChanged(nameof(IsAiSchedule));
        OnPropertyChanged(nameof(ShowAiAutoUpdateOptions));
        OnPropertyChanged(nameof(ShowAiProviderMissingWarning));
    }

    partial void OnAutoUpdateScopeChanged(AutoUpdateScope value) => NotifyScheduleTypeChanged();

    partial void OnAiRiskToleranceChanged(AiAutoUpdateRiskTolerance value)
    {
        OnPropertyChanged(nameof(AiInstallsSafeOnly));
        OnPropertyChanged(nameof(AiInstallsSafeAndCaution));
    }

    /// <summary>Opens the list of drivers the custom schedule may update.</summary>
    [RelayCommand]
    private async Task ChooseAutoUpdateDriversAsync(CancellationToken cancellationToken = default)
    {
        _autoUpdateSelectionWindowOpener?.Open();
        await RefreshSelectedDriverCountAsync(cancellationToken).ConfigureAwait(true);
    }

    private async Task RefreshSelectedDriverCountAsync(CancellationToken cancellationToken = default)
    {
        if (_autoUpdateSelectionStore is null)
        {
            return;
        }

        try
        {
            var selection = await _autoUpdateSelectionStore.LoadAsync(cancellationToken).ConfigureAwait(true);
            SelectedDriverCount = selection.DeviceIds.Count;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read how many drivers are selected for automatic updates");
        }
    }

    /// <summary>AI-driven automatic updates install nothing while no provider is configured.</summary>
    public bool ShowAiProviderMissingWarning =>
        ShowAiAutoUpdateOptions && SelectedAiProvider == AiProvider.Off;

    public bool IsGeminiSelected => SelectedAiProvider == AiProvider.Gemini;

    public bool IsOllamaSelected => SelectedAiProvider == AiProvider.Ollama;

    public bool CanStartMinimized =>
        StartWithWindows && CloseBehavior == WindowCloseBehavior.KeepRunningInBackground;

    public SettingsViewModel(
        ISettingsStore settingsStore,
        ISchedulerService schedulerService,
        ILogger<SettingsViewModel> logger,
        ILocalizationService? localizationService = null,
        IAppUpdater? appUpdater = null,
        IAppUpdatePrompt? appUpdatePrompt = null,
        ILogCleanupService? logCleanupService = null,
        IDriverCacheStore? driverCacheStore = null,
        ApplicationBehaviorState? applicationBehaviorState = null,
        IApplicationStartupService? applicationStartupService = null,
        IAutoUpdateSelectionStore? autoUpdateSelectionStore = null,
        IAutoUpdateSelectionWindowOpener? autoUpdateSelectionWindowOpener = null)
    {
        ArgumentNullException.ThrowIfNull(settingsStore);
        ArgumentNullException.ThrowIfNull(schedulerService);
        ArgumentNullException.ThrowIfNull(logger);
        _settingsStore = settingsStore;
        _schedulerService = schedulerService;
        _localizationService = localizationService;
        _appUpdater = appUpdater;
        _appUpdatePrompt = appUpdatePrompt;
        _logCleanupService = logCleanupService;
        _driverCacheStore = driverCacheStore;
        _applicationBehaviorState = applicationBehaviorState;
        _applicationStartupService = applicationStartupService;
        _autoUpdateSelectionStore = autoUpdateSelectionStore;
        _autoUpdateSelectionWindowOpener = autoUpdateSelectionWindowOpener;
        _logger = logger;
    }

    private bool CanRunUpdateCheck() => _appUpdater is not null && !IsCheckingForUpdates;

    [RelayCommand(CanExecute = nameof(CanRunUpdateCheck))]
    private async Task CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        if (_appUpdater is null)
        {
            return;
        }

        IsCheckingForUpdates = true;
        StatusText = "Checking for app updates...";
        try
        {
            var result = await _appUpdater.CheckForUpdatesAsync(cancellationToken).ConfigureAwait(true);
            if (!result.IsUpdateAvailable)
            {
                StatusText = result.Status switch
                {
                    AppUpdateCheckStatus.NotInstalled =>
                        "Automatic updates require the Setup installer. Download and run the latest Setup from GitHub.",
                    AppUpdateCheckStatus.NotConfigured => "No app update source is configured.",
                    AppUpdateCheckStatus.Failed => "Could not check for updates. See logs for details.",
                    _ => "You're on the latest version."
                };
                return;
            }

            _logger.LogInformation("App update {Version} is available (checked from Settings)", result.Version);
            StatusText = $"App update {result.Version} is available.";

            // Offer to install now. DownloadAndApplyAsync restarts onto the new version on
            // success, so control does not return past it.
            if (_appUpdatePrompt is not null && _appUpdatePrompt.Confirm(result.Version))
            {
                StatusText = $"Downloading app update {result.Version}...";
                var progress = new Progress<int>(percent =>
                    StatusText = $"Downloading app update {result.Version}... {percent}%");
                await _appUpdater.DownloadAndApplyAsync(progress, cancellationToken).ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Checking for an app update from Settings failed");
            StatusText = "Could not check for updates. See logs for details.";
        }
        finally
        {
            IsCheckingForUpdates = false;
        }
    }

    partial void OnScheduleModeChanged(ScheduleMode value)
    {
        OnPropertyChanged(nameof(ShowAutoUpdateWarning));
        NotifyScheduleTypeChanged();
        if (value != ScheduleMode.ScanAndUpdate)
        {
            AcceptedAutoUpdateRisk = false;
        }
    }

    [RelayCommand]
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        try
        {
            var settings = await _settingsStore.LoadAsync(cancellationToken).ConfigureAwait(true);
            ApplyFromSettings(settings);
            await RefreshSelectedDriverCountAsync(cancellationToken).ConfigureAwait(true);
            StatusText = $"Loaded from {_settingsStore.SettingsPath}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load settings");
            StatusText = $"Could not load: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        try
        {
            var settings = BuildSettings();
            await _settingsStore.SaveAsync(settings, cancellationToken).ConfigureAwait(true);
            _applicationBehaviorState?.Apply(settings.Application);
            _logger.LogInformation(
                "Application behavior saved: closeBehavior={CloseBehavior}, startWithWindows={StartWithWindows}, startMinimized={StartMinimized}",
                settings.Application.CloseBehavior,
                settings.Application.StartWithWindows,
                settings.Application.StartMinimized);

            var startupWarning = string.Empty;
            if (_applicationStartupService is not null)
            {
                try
                {
                    await _applicationStartupService.ApplyAsync(
                        settings.Application.StartWithWindows,
                        settings.Application.StartMinimized,
                        cancellationToken).ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    startupWarning = " Windows startup could not be updated. See logs for details.";
                    _logger.LogError(
                        ex,
                        "Failed to apply Windows startup setting: enabled={Enabled}, startMinimized={StartMinimized}",
                        settings.Application.StartWithWindows,
                        settings.Application.StartMinimized);
                }
            }

            var deletedLogFiles = 0;
            if (_logCleanupService is not null)
            {
                deletedLogFiles = await _logCleanupService.CleanupAsync(
                    settings.LogCleanup,
                    cancellationToken).ConfigureAwait(true);
            }

            var scheduleResult = await _schedulerService.ApplyAsync(
                ScheduleMode,
                ScheduleCadence,
                ScheduleTimeOfDay,
                ScheduleDayOfWeek,
                cancellationToken).ConfigureAwait(true);

            if (scheduleResult.IsFailure)
            {
                StatusText = $"Saved settings, but schedule update failed: {scheduleResult.Error.Message}";
                return;
            }

            _localizationService?.ApplyLanguage(SelectedLanguage);

            StatusText = deletedLogFiles > 0
                ? $"Settings saved. Removed {deletedLogFiles} old log file(s).{startupWarning}"
                : $"Settings saved.{startupWarning}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save settings");
            StatusText = $"Could not save: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanSave()
    {
        if (ScheduleMode == ScheduleMode.ScanAndUpdate && !AcceptedAutoUpdateRisk)
        {
            return false;
        }
        return !IsBusy;
    }

    private bool CanClearDriverCache() => _driverCacheStore is not null && !IsBusy;

    [RelayCommand]
    private void AddGeminiApiKey()
    {
        GeminiApiKeys.Add(new GeminiApiKeyEntryViewModel());
        _logger.LogInformation(
            "Added Gemini API key field in Settings: configuredFields={FieldCount}",
            GeminiApiKeys.Count);
    }

    [RelayCommand]
    private void RemoveGeminiApiKey(GeminiApiKeyEntryViewModel? entry)
    {
        if (entry is null || !GeminiApiKeys.Remove(entry))
        {
            return;
        }

        if (GeminiApiKeys.Count == 0)
        {
            GeminiApiKeys.Add(new GeminiApiKeyEntryViewModel());
        }

        OnPropertyChanged(nameof(GeminiApiKey));
        _logger.LogInformation(
            "Removed Gemini API key field in Settings: remainingFields={FieldCount}",
            GeminiApiKeys.Count);
    }

    [RelayCommand(CanExecute = nameof(CanClearDriverCache))]
    private async Task ClearDriverCacheAsync(CancellationToken cancellationToken)
    {
        if (_driverCacheStore is null)
        {
            return;
        }

        IsBusy = true;
        StatusText = "Clearing cached driver scan results...";
        _logger.LogInformation("User requested clearing the driver update cache from Settings");
        try
        {
            var removedUpdates = await _driverCacheStore
                .ClearAsync(cancellationToken)
                .ConfigureAwait(true);
            StatusText =
                $"Driver update cache cleared. Removed {removedUpdates} cached update result(s). The next scan starts from scratch.";
            _logger.LogInformation(
                "Settings driver cache clear completed: {UpdateCount} cached update result(s) removed",
                removedUpdates);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StatusText = "Driver cache clear cancelled.";
            _logger.LogInformation("Settings driver cache clear cancelled");
        }
        catch (Exception ex)
        {
            StatusText = $"Could not clear driver update cache: {ex.Message}";
            _logger.LogError(ex, "Settings driver cache clear failed");
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnAcceptedAutoUpdateRiskChanged(bool value) => SaveCommand.NotifyCanExecuteChanged();

    partial void OnCloseBehaviorChanged(WindowCloseBehavior value)
    {
        if (!CanStartMinimized)
        {
            StartMinimized = false;
        }
    }

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (!CanStartMinimized)
        {
            StartMinimized = false;
        }
    }

    partial void OnIsBusyChanged(bool value)
    {
        SaveCommand.NotifyCanExecuteChanged();
        ClearDriverCacheCommand.NotifyCanExecuteChanged();
    }

    internal AppSettings BuildSettings() => new()
    {
        Application = new ApplicationSettings
        {
            CloseBehavior = CloseBehavior,
            StartWithWindows = StartWithWindows,
            StartMinimized = CanStartMinimized && StartMinimized
        },
        Catalog = new CatalogSettings
        {
            Enabled = EnableMicrosoftCatalog,
            MaxConcurrentSearches = _loadedCatalog.MaxConcurrentSearches,
            CacheDuration = _loadedCatalog.CacheDuration,
            MaxRetries = _loadedCatalog.MaxRetries,
            RequestTimeout = _loadedCatalog.RequestTimeout
        },
        Backup = new BackupSettings
        {
            RetentionDays = BackupRetentionDays,
            // No UI field: keep the configured folder instead of silently reverting to the
            // default and orphaning every backup already written there.
            RootPath = _loadedBackup.RootPath
        },
        History = new HistorySettings
        {
            DatabasePath = _loadedHistory.DatabasePath
        },
        Updater = new UpdaterSettings
        {
            WindowsUpdateEnabled = EnableWindowsUpdate,
            OemSourcesEnabled = EnableOemHints,
            CheckOnStartup = CheckForUpdatesOnStartup,
            AutoApply = AutoInstallAppUpdates,
            // Preserve the feed/repo settings that have no UI so a save does not reset them.
            GitHubRepoUrl = _loadedUpdater.GitHubRepoUrl,
            FeedUrl = _loadedUpdater.FeedUrl,
            AllowPrerelease = _loadedUpdater.AllowPrerelease
        },
        Schedule = new ScheduleSettings
        {
            Mode = ScheduleMode,
            Cadence = ScheduleCadence,
            TimeOfDay = ScheduleTimeOfDay,
            DayOfWeek = ScheduleDayOfWeek,
            AutoUpdateScope = AutoUpdateScope,
            AiRiskTolerance = AiRiskTolerance
        },
        Language = new LanguageSettings
        {
            Language = SelectedLanguage
        },
        Scraper = new ScraperSettings
        {
            EnablePlaywrightFallback = EnablePlaywrightFallback
        },
        Ai = BuildAiSettings(),
        LogCleanup = new LogCleanupSettings
        {
            Enabled = EnableAutomaticLogCleanup,
            RetentionDays = Math.Clamp(
                LogRetentionDays,
                LogCleanupSettings.MinimumRetentionDays,
                LogCleanupSettings.MaximumRetentionDays)
        },
        Onboarding = new OnboardingSettings
        {
            LastShownVersion = _loadedOnboarding.LastShownVersion,
            ShowOnStartup = ShowGuideOnStartup
        }
    };

    internal void ApplyFromSettings(AppSettings settings)
    {
        var application = settings.Application ?? new ApplicationSettings();
        CloseBehavior = application.CloseBehavior == WindowCloseBehavior.KeepRunningInBackground
            ? WindowCloseBehavior.KeepRunningInBackground
            : WindowCloseBehavior.ExitApplication;
        StartWithWindows = application.StartWithWindows;
        StartMinimized = CanStartMinimized && application.StartMinimized;
        _loadedCatalog = settings.Catalog;
        _loadedUpdater = settings.Updater;
        _loadedBackup = settings.Backup;
        _loadedHistory = settings.History ?? new HistorySettings();
        _loadedOnboarding = settings.Onboarding ?? new OnboardingSettings();
        ShowGuideOnStartup = _loadedOnboarding.ShowOnStartup;
        EnableWindowsUpdate = settings.Updater.WindowsUpdateEnabled;
        EnableMicrosoftCatalog = settings.Catalog.Enabled;
        EnableOemHints = settings.Updater.OemSourcesEnabled;
        CheckForUpdatesOnStartup = settings.Updater.CheckOnStartup;
        AutoInstallAppUpdates = settings.Updater.AutoApply;
        BackupRetentionDays = settings.Backup.RetentionDays;
        ScheduleMode = settings.Schedule.Mode;
        ScheduleCadence = settings.Schedule.Cadence;
        ScheduleTimeOfDay = settings.Schedule.TimeOfDay;
        ScheduleDayOfWeek = settings.Schedule.DayOfWeek;
        AutoUpdateScope = settings.Schedule.AutoUpdateScope;
        AiRiskTolerance = settings.Schedule.AiRiskTolerance;
        SelectedLanguage = settings.Language.Language;
        SelectedAiResponseLanguage = settings.Ai.ResponseLanguage is AppLanguage.Hebrew
            ? AppLanguage.Hebrew
            : AppLanguage.English;
        AcceptedAutoUpdateRisk = settings.Schedule.Mode == ScheduleMode.ScanAndUpdate;
        EnablePlaywrightFallback = settings.Scraper.EnablePlaywrightFallback;
        SelectedAiProvider = settings.Ai.Provider;
        SetGeminiApiKeys(settings.Ai.GetGeminiApiKeys());
        GeminiModel = settings.Ai.GeminiModel;
        EnableAiWebSearch = settings.Ai.EnableWebSearch;
        GeminiDailyRequestLimit = Math.Max(0, settings.Ai.GeminiDailyRequestLimit);
        ShowAiScanUsageWarning = settings.Ai.ShowAiScanUsageWarning;
        OllamaBaseUrl = settings.Ai.OllamaBaseUrl;
        OllamaModel = settings.Ai.OllamaModel;
        EnableAutomaticLogCleanup = settings.LogCleanup.Enabled;
        LogRetentionDays = Math.Clamp(
            settings.LogCleanup.RetentionDays,
            LogCleanupSettings.MinimumRetentionDays,
            LogCleanupSettings.MaximumRetentionDays);
    }

    private AiSettings BuildAiSettings()
    {
        var keys = GeminiApiKeys
            .Select(entry => entry.Value.Trim())
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        _logger.LogInformation(
            "Saving Gemini configuration with {KeyCount} unique API key(s); key values are not logged",
            keys.Count);

        return new AiSettings
        {
            Provider = SelectedAiProvider,
            ResponseLanguage = SelectedAiResponseLanguage,
            GeminiApiKey = keys.FirstOrDefault() ?? string.Empty,
            GeminiApiKeys = keys,
            GeminiModel = string.IsNullOrWhiteSpace(GeminiModel) ? new AiSettings().GeminiModel : GeminiModel.Trim(),
            EnableWebSearch = EnableAiWebSearch,
            GeminiDailyRequestLimit = Math.Max(0, GeminiDailyRequestLimit),
            ShowAiScanUsageWarning = ShowAiScanUsageWarning,
            OllamaBaseUrl = string.IsNullOrWhiteSpace(OllamaBaseUrl) ? "http://localhost:11434" : OllamaBaseUrl.Trim(),
            OllamaModel = string.IsNullOrWhiteSpace(OllamaModel) ? "llama3.1" : OllamaModel.Trim()
        };
    }

    private void SetGeminiApiKeys(IReadOnlyList<string> keys)
    {
        GeminiApiKeys.Clear();
        foreach (var key in keys)
        {
            GeminiApiKeys.Add(new GeminiApiKeyEntryViewModel { Value = key });
        }

        if (GeminiApiKeys.Count == 0)
        {
            GeminiApiKeys.Add(new GeminiApiKeyEntryViewModel());
        }

        OnPropertyChanged(nameof(GeminiApiKey));
        _logger.LogInformation(
            "Loaded Gemini configuration with {KeyCount} API key(s); key values are not logged",
            keys.Count);
    }
}

public partial class GeminiApiKeyEntryViewModel : ObservableObject
{
    [ObservableProperty]
    private string _value = string.Empty;
}
