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
    private readonly ILogger<SettingsViewModel> _logger;

    public IReadOnlyList<ScheduleMode> AvailableModes { get; } = Enum.GetValues<ScheduleMode>().ToArray();
    public IReadOnlyList<ScheduleCadence> AvailableCadences { get; } = Enum.GetValues<ScheduleCadence>().ToArray();
    public IReadOnlyList<DayOfWeek> AvailableDays { get; } = Enum.GetValues<DayOfWeek>().ToArray();
    public IReadOnlyList<AppLanguage> AvailableLanguages { get; } = Enum.GetValues<AppLanguage>().ToArray();

    [ObservableProperty] private AppLanguage _selectedLanguage = AppLanguage.SystemDefault;

    [ObservableProperty] private bool _enableWindowsUpdate = true;
    [ObservableProperty] private bool _enableMicrosoftCatalog;
    [ObservableProperty] private bool _enableOemHints = true;

    [ObservableProperty] private bool _createRestorePoint = true;
    [ObservableProperty] private bool _backupBeforeInstall = true;
    [ObservableProperty] private int _backupRetentionDays = 30;

    [ObservableProperty] private ScheduleMode _scheduleMode = ScheduleMode.Manual;
    [ObservableProperty] private ScheduleCadence _scheduleCadence = ScheduleCadence.Weekly;
    [ObservableProperty] private TimeOnly _scheduleTimeOfDay = new(9, 0);
    [ObservableProperty] private DayOfWeek _scheduleDayOfWeek = DayOfWeek.Monday;
    [ObservableProperty] private bool _acceptedAutoUpdateRisk;

    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private bool _isBusy;

    public string SettingsPath => _settingsStore.SettingsPath;

    public bool ShowAutoUpdateWarning => ScheduleMode == ScheduleMode.ScanAndUpdate;

    public SettingsViewModel(
        ISettingsStore settingsStore,
        ISchedulerService schedulerService,
        ILogger<SettingsViewModel> logger,
        ILocalizationService? localizationService = null)
    {
        ArgumentNullException.ThrowIfNull(settingsStore);
        ArgumentNullException.ThrowIfNull(schedulerService);
        ArgumentNullException.ThrowIfNull(logger);
        _settingsStore = settingsStore;
        _schedulerService = schedulerService;
        _localizationService = localizationService;
        _logger = logger;
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
        }
    };

    internal void ApplyFromSettings(AppSettings settings)
    {
        EnableMicrosoftCatalog = settings.Catalog.Enabled;
        BackupRetentionDays = settings.Backup.RetentionDays;
        ScheduleMode = settings.Schedule.Mode;
        ScheduleCadence = settings.Schedule.Cadence;
        ScheduleTimeOfDay = settings.Schedule.TimeOfDay;
        ScheduleDayOfWeek = settings.Schedule.DayOfWeek;
        SelectedLanguage = settings.Language.Language;
        AcceptedAutoUpdateRisk = settings.Schedule.Mode == ScheduleMode.ScanAndUpdate;
    }
}
