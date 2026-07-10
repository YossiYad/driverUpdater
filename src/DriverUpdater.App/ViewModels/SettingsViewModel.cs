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
    private readonly ILogger<SettingsViewModel> _logger;

    public IReadOnlyList<ScheduleMode> AvailableModes { get; } = Enum.GetValues<ScheduleMode>().ToArray();
    public IReadOnlyList<ScheduleCadence> AvailableCadences { get; } = Enum.GetValues<ScheduleCadence>().ToArray();
    public IReadOnlyList<DayOfWeek> AvailableDays { get; } = Enum.GetValues<DayOfWeek>().ToArray();
    public IReadOnlyList<AppLanguage> AvailableLanguages { get; } = Enum.GetValues<AppLanguage>().ToArray();
    public IReadOnlyList<AiProvider> AvailableAiProviders { get; } = Enum.GetValues<AiProvider>().ToArray();

    [ObservableProperty] private AppLanguage _selectedLanguage = AppLanguage.SystemDefault;

    [ObservableProperty] private bool _enableWindowsUpdate = true;
    [ObservableProperty] private bool _enableMicrosoftCatalog = true;
    [ObservableProperty] private bool _enableOemHints = true;

    [ObservableProperty] private bool _createRestorePoint = true;
    [ObservableProperty] private bool _backupBeforeInstall = true;
    [ObservableProperty] private int _backupRetentionDays = 30;

    [ObservableProperty] private ScheduleMode _scheduleMode = ScheduleMode.Manual;
    [ObservableProperty] private ScheduleCadence _scheduleCadence = ScheduleCadence.Weekly;
    [ObservableProperty] private TimeOnly _scheduleTimeOfDay = new(9, 0);
    [ObservableProperty] private DayOfWeek _scheduleDayOfWeek = DayOfWeek.Monday;
    [ObservableProperty] private bool _acceptedAutoUpdateRisk;

    [ObservableProperty] private bool _enablePlaywrightFallback;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGeminiSelected))]
    [NotifyPropertyChangedFor(nameof(IsOllamaSelected))]
    private AiProvider _selectedAiProvider = AiProvider.Off;
    [ObservableProperty] private string _geminiApiKey = string.Empty;
    [ObservableProperty] private string _geminiModel = "gemini-2.5-flash";
    [ObservableProperty] private bool _enableAiWebSearch = true;
    [ObservableProperty] private string _ollamaBaseUrl = "http://localhost:11434";
    [ObservableProperty] private string _ollamaModel = "llama3.1";

    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private bool _isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckForUpdatesCommand))]
    private bool _isCheckingForUpdates;

    /// <summary>True when app self-updating is wired up (Velopack), so the button is worth showing.</summary>
    public bool CanCheckForUpdates => _appUpdater is not null;

    public string SettingsPath => _settingsStore.SettingsPath;

    public bool ShowAutoUpdateWarning => ScheduleMode == ScheduleMode.ScanAndUpdate;

    public bool IsGeminiSelected => SelectedAiProvider == AiProvider.Gemini;

    public bool IsOllamaSelected => SelectedAiProvider == AiProvider.Ollama;

    public SettingsViewModel(
        ISettingsStore settingsStore,
        ISchedulerService schedulerService,
        ILogger<SettingsViewModel> logger,
        ILocalizationService? localizationService = null,
        IAppUpdater? appUpdater = null,
        IAppUpdatePrompt? appUpdatePrompt = null)
    {
        ArgumentNullException.ThrowIfNull(settingsStore);
        ArgumentNullException.ThrowIfNull(schedulerService);
        ArgumentNullException.ThrowIfNull(logger);
        _settingsStore = settingsStore;
        _schedulerService = schedulerService;
        _localizationService = localizationService;
        _appUpdater = appUpdater;
        _appUpdatePrompt = appUpdatePrompt;
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
                StatusText = "You're on the latest version.";
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

            StatusText = "Settings saved.";
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

    partial void OnAcceptedAutoUpdateRiskChanged(bool value) => SaveCommand.NotifyCanExecuteChanged();
    partial void OnIsBusyChanged(bool value) => SaveCommand.NotifyCanExecuteChanged();

    internal AppSettings BuildSettings() => new()
    {
        Catalog = new CatalogSettings
        {
            Enabled = EnableMicrosoftCatalog,
            MaxConcurrentSearches = 4,
            CacheDuration = TimeSpan.FromHours(24)
        },
        Backup = new BackupSettings
        {
            RetentionDays = BackupRetentionDays
        },
        History = new HistorySettings(),
        Updater = new UpdaterSettings
        {
            WindowsUpdateEnabled = EnableWindowsUpdate,
            OemSourcesEnabled = EnableOemHints
        },
        Schedule = new ScheduleSettings
        {
            Mode = ScheduleMode,
            Cadence = ScheduleCadence,
            TimeOfDay = ScheduleTimeOfDay,
            DayOfWeek = ScheduleDayOfWeek
        },
        Language = new LanguageSettings
        {
            Language = SelectedLanguage
        },
        Scraper = new ScraperSettings
        {
            EnablePlaywrightFallback = EnablePlaywrightFallback
        },
        Ai = new AiSettings
        {
            Provider = SelectedAiProvider,
            GeminiApiKey = GeminiApiKey,
            GeminiModel = string.IsNullOrWhiteSpace(GeminiModel) ? "gemini-2.5-flash" : GeminiModel.Trim(),
            EnableWebSearch = EnableAiWebSearch,
            OllamaBaseUrl = string.IsNullOrWhiteSpace(OllamaBaseUrl) ? "http://localhost:11434" : OllamaBaseUrl.Trim(),
            OllamaModel = string.IsNullOrWhiteSpace(OllamaModel) ? "llama3.1" : OllamaModel.Trim()
        }
    };

    internal void ApplyFromSettings(AppSettings settings)
    {
        EnableWindowsUpdate = settings.Updater.WindowsUpdateEnabled;
        EnableMicrosoftCatalog = settings.Catalog.Enabled;
        EnableOemHints = settings.Updater.OemSourcesEnabled;
        BackupRetentionDays = settings.Backup.RetentionDays;
        ScheduleMode = settings.Schedule.Mode;
        ScheduleCadence = settings.Schedule.Cadence;
        ScheduleTimeOfDay = settings.Schedule.TimeOfDay;
        ScheduleDayOfWeek = settings.Schedule.DayOfWeek;
        SelectedLanguage = settings.Language.Language;
        AcceptedAutoUpdateRisk = settings.Schedule.Mode == ScheduleMode.ScanAndUpdate;
        EnablePlaywrightFallback = settings.Scraper.EnablePlaywrightFallback;
        SelectedAiProvider = settings.Ai.Provider;
        GeminiApiKey = settings.Ai.GeminiApiKey;
        GeminiModel = settings.Ai.GeminiModel;
        EnableAiWebSearch = settings.Ai.EnableWebSearch;
        OllamaBaseUrl = settings.Ai.OllamaBaseUrl;
        OllamaModel = settings.Ai.OllamaModel;
    }
}
