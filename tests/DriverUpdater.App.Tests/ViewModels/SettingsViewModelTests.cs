using DriverUpdater.App.Services;
using DriverUpdater.App.ViewModels;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using DriverUpdater.Core.Options;
using DriverUpdater.Core.Results;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriverUpdater.App.Tests.ViewModels;

public class SettingsViewModelTests
{
    [WpfFact]
    public async Task LoadAsync_reads_application_behavior_settings()
    {
        var store = new FakeStore(new AppSettings
        {
            Application = new ApplicationSettings
            {
                CloseBehavior = WindowCloseBehavior.KeepRunningInBackground,
                StartWithWindows = true,
                StartMinimized = true
            }
        });
        var vm = new SettingsViewModel(
            store,
            new FakeScheduler(),
            NullLogger<SettingsViewModel>.Instance);

        await vm.LoadAsync();

        vm.CloseBehavior.Should().Be(WindowCloseBehavior.KeepRunningInBackground);
        vm.StartWithWindows.Should().BeTrue();
        vm.StartMinimized.Should().BeTrue();
        vm.CanStartMinimized.Should().BeTrue();
    }

    [WpfFact]
    public async Task SaveAsync_applies_application_and_Windows_startup_settings()
    {
        var store = new FakeStore(new AppSettings());
        var startup = new FakeApplicationStartupService();
        var state = new ApplicationBehaviorState();
        var vm = new SettingsViewModel(
            store,
            new FakeScheduler(),
            NullLogger<SettingsViewModel>.Instance,
            applicationBehaviorState: state,
            applicationStartupService: startup);
        await vm.LoadAsync();
        vm.CloseBehavior = WindowCloseBehavior.KeepRunningInBackground;
        vm.StartWithWindows = true;
        vm.StartMinimized = true;

        await vm.SaveAsync();

        store.Saved!.Application.CloseBehavior
            .Should().Be(WindowCloseBehavior.KeepRunningInBackground);
        store.Saved.Application.StartWithWindows.Should().BeTrue();
        store.Saved.Application.StartMinimized.Should().BeTrue();
        startup.ApplyCalls.Should().Be(1);
        startup.StartWithWindows.Should().BeTrue();
        startup.StartMinimized.Should().BeTrue();
        state.ShouldStartHidden.Should().BeTrue();
    }

    [WpfFact]
    public void Start_minimized_is_cleared_when_background_mode_is_disabled()
    {
        var vm = new SettingsViewModel(
            new FakeStore(new AppSettings()),
            new FakeScheduler(),
            NullLogger<SettingsViewModel>.Instance)
        {
            CloseBehavior = WindowCloseBehavior.KeepRunningInBackground,
            StartWithWindows = true,
            StartMinimized = true
        };

        vm.CloseBehavior = WindowCloseBehavior.ExitApplication;

        vm.CanStartMinimized.Should().BeFalse();
        vm.StartMinimized.Should().BeFalse();
    }

    [WpfFact]
    public async Task LoadAsync_pulls_values_from_store()
    {
        var store = new FakeStore(new AppSettings
        {
            Catalog = new CatalogSettings { Enabled = true },
            Backup = new BackupSettings { RetentionDays = 90 },
            Schedule = new ScheduleSettings
            {
                Mode = ScheduleMode.ScanOnly,
                Cadence = ScheduleCadence.Weekly,
                TimeOfDay = new TimeOnly(8, 15),
                DayOfWeek = DayOfWeek.Thursday
            }
        });
        var scheduler = new FakeScheduler();
        var vm = new SettingsViewModel(store, scheduler, NullLogger<SettingsViewModel>.Instance);

        await vm.LoadAsync();

        vm.EnableMicrosoftCatalog.Should().BeTrue();
        vm.BackupRetentionDays.Should().Be(90);
        vm.ScheduleMode.Should().Be(ScheduleMode.ScanOnly);
        vm.ScheduleCadence.Should().Be(ScheduleCadence.Weekly);
        vm.ScheduleTimeOfDay.Should().Be(new TimeOnly(8, 15));
        vm.ScheduleDayOfWeek.Should().Be(DayOfWeek.Thursday);
    }

    [WpfFact]
    public async Task LoadAsync_reads_log_cleanup_settings()
    {
        var store = new FakeStore(new AppSettings
        {
            LogCleanup = new LogCleanupSettings
            {
                Enabled = false,
                RetentionDays = 30
            }
        });
        var vm = new SettingsViewModel(
            store,
            new FakeScheduler(),
            NullLogger<SettingsViewModel>.Instance);

        await vm.LoadAsync();

        vm.EnableAutomaticLogCleanup.Should().BeFalse();
        vm.LogRetentionDays.Should().Be(30);
    }

    [WpfFact]
    public async Task SaveAsync_persists_and_immediately_applies_log_cleanup_settings()
    {
        var store = new FakeStore(new AppSettings());
        var cleanup = new FakeLogCleanupService { DeletedFiles = 2 };
        var vm = new SettingsViewModel(
            store,
            new FakeScheduler(),
            NullLogger<SettingsViewModel>.Instance,
            logCleanupService: cleanup);
        await vm.LoadAsync();
        vm.EnableAutomaticLogCleanup = true;
        vm.LogRetentionDays = 14;

        await vm.SaveAsync();

        store.Saved!.LogCleanup.Enabled.Should().BeTrue();
        store.Saved.LogCleanup.RetentionDays.Should().Be(14);
        cleanup.LastSettings.Should().BeEquivalentTo(store.Saved.LogCleanup);
        vm.StatusText.Should().Be("Settings saved. Removed 2 old log file(s).");
    }

    [WpfFact]
    public async Task SaveAsync_writes_to_store_and_calls_scheduler()
    {
        var store = new FakeStore(new AppSettings());
        var scheduler = new FakeScheduler();
        var vm = new SettingsViewModel(store, scheduler, NullLogger<SettingsViewModel>.Instance);
        await vm.LoadAsync();

        vm.EnableMicrosoftCatalog = true;
        vm.BackupRetentionDays = 14;
        vm.ScheduleMode = ScheduleMode.ScanOnly;
        vm.ScheduleCadence = ScheduleCadence.Daily;
        vm.ScheduleTimeOfDay = new TimeOnly(6, 0);

        await vm.SaveAsync();

        store.Saved.Should().NotBeNull();
        store.Saved!.Catalog.Enabled.Should().BeTrue();
        store.Saved.Backup.RetentionDays.Should().Be(14);
        store.Saved.Schedule.Mode.Should().Be(ScheduleMode.ScanOnly);
        scheduler.ApplyCalls.Should().Be(1);
        vm.StatusText.Should().Be("Settings saved.");
    }

    [WpfFact]
    public async Task SaveAsync_preserves_catalog_tuning_that_is_not_exposed_in_the_ui()
    {
        var catalog = new CatalogSettings
        {
            Enabled = true,
            MaxConcurrentSearches = 7,
            CacheDuration = TimeSpan.FromHours(12),
            MaxRetries = 1,
            RequestTimeout = TimeSpan.FromSeconds(15)
        };
        var store = new FakeStore(new AppSettings { Catalog = catalog });
        var vm = new SettingsViewModel(
            store,
            new FakeScheduler(),
            NullLogger<SettingsViewModel>.Instance);
        await vm.LoadAsync();

        await vm.SaveAsync();

        store.Saved.Should().NotBeNull();
        store.Saved!.Catalog.MaxConcurrentSearches.Should().Be(7);
        store.Saved.Catalog.CacheDuration.Should().Be(TimeSpan.FromHours(12));
        store.Saved.Catalog.MaxRetries.Should().Be(1);
        store.Saved.Catalog.RequestTimeout.Should().Be(TimeSpan.FromSeconds(15));
    }

    [WpfFact]
    public async Task LoadAsync_reads_source_toggles_from_store()
    {
        var store = new FakeStore(new AppSettings
        {
            Catalog = new CatalogSettings { Enabled = false },
            Updater = new UpdaterSettings { WindowsUpdateEnabled = false, OemSourcesEnabled = false }
        });
        var vm = new SettingsViewModel(store, new FakeScheduler(), NullLogger<SettingsViewModel>.Instance);

        await vm.LoadAsync();

        vm.EnableWindowsUpdate.Should().BeFalse();
        vm.EnableMicrosoftCatalog.Should().BeFalse();
        vm.EnableOemHints.Should().BeFalse();
    }

    [WpfFact]
    public async Task SaveAsync_persists_source_toggles()
    {
        var store = new FakeStore(new AppSettings());
        var vm = new SettingsViewModel(store, new FakeScheduler(), NullLogger<SettingsViewModel>.Instance);
        await vm.LoadAsync();

        vm.EnableWindowsUpdate = false;
        vm.EnableMicrosoftCatalog = false;
        vm.EnableOemHints = false;

        await vm.SaveAsync();

        store.Saved.Should().NotBeNull();
        store.Saved!.Updater.WindowsUpdateEnabled.Should().BeFalse();
        store.Saved.Catalog.Enabled.Should().BeFalse();
        store.Saved.Updater.OemSourcesEnabled.Should().BeFalse();
    }

    [WpfFact]
    public void Save_command_is_disabled_for_scan_and_update_until_risk_accepted()
    {
        var store = new FakeStore(new AppSettings());
        var scheduler = new FakeScheduler();
        var vm = new SettingsViewModel(store, scheduler, NullLogger<SettingsViewModel>.Instance);

        vm.ScheduleMode = ScheduleMode.ScanAndUpdate;
        vm.AcceptedAutoUpdateRisk = false;
        vm.SaveCommand.CanExecute(null).Should().BeFalse();

        vm.AcceptedAutoUpdateRisk = true;
        vm.SaveCommand.CanExecute(null).Should().BeTrue();
    }

    [WpfFact]
    public void Changing_schedule_mode_away_from_scan_and_update_clears_risk_acceptance()
    {
        var store = new FakeStore(new AppSettings());
        var scheduler = new FakeScheduler();
        var vm = new SettingsViewModel(store, scheduler, NullLogger<SettingsViewModel>.Instance);

        vm.ScheduleMode = ScheduleMode.ScanAndUpdate;
        vm.AcceptedAutoUpdateRisk = true;
        vm.ScheduleMode = ScheduleMode.ScanOnly;

        vm.AcceptedAutoUpdateRisk.Should().BeFalse();
    }

    [WpfFact]
    public async Task SaveAsync_writes_language_setting_to_store()
    {
        var store = new FakeStore(new AppSettings());
        var scheduler = new FakeScheduler();
        var vm = new SettingsViewModel(store, scheduler, NullLogger<SettingsViewModel>.Instance);
        await vm.LoadAsync();

        vm.SelectedLanguage = AppLanguage.Hebrew;
        await vm.SaveAsync();

        store.Saved.Should().NotBeNull();
        store.Saved!.Language.Language.Should().Be(AppLanguage.Hebrew);
    }

    [WpfFact]
    public async Task LoadAsync_reads_language_setting_from_store()
    {
        var store = new FakeStore(new AppSettings
        {
            Language = new LanguageSettings { Language = AppLanguage.English }
        });
        var scheduler = new FakeScheduler();
        var vm = new SettingsViewModel(store, scheduler, NullLogger<SettingsViewModel>.Instance);

        await vm.LoadAsync();

        vm.SelectedLanguage.Should().Be(AppLanguage.English);
    }

    [WpfFact]
    public async Task LoadAsync_reads_ai_settings_from_store()
    {
        var store = new FakeStore(new AppSettings
        {
            Ai = new AiSettings
            {
                Provider = AiProvider.Gemini,
                ResponseLanguage = AppLanguage.Hebrew,
                GeminiApiKey = "abc123",
                GeminiModel = "gemini-2.0-flash",
                EnableWebSearch = false,
                OllamaBaseUrl = "http://host:1234",
                OllamaModel = "mistral"
            }
        });
        var scheduler = new FakeScheduler();
        var vm = new SettingsViewModel(store, scheduler, NullLogger<SettingsViewModel>.Instance);

        await vm.LoadAsync();

        vm.SelectedAiProvider.Should().Be(AiProvider.Gemini);
        vm.SelectedAiResponseLanguage.Should().Be(AppLanguage.Hebrew);
        vm.GeminiApiKey.Should().Be("abc123");
        vm.GeminiModel.Should().Be("gemini-2.0-flash");
        vm.EnableAiWebSearch.Should().BeFalse();
        vm.OllamaBaseUrl.Should().Be("http://host:1234");
        vm.OllamaModel.Should().Be("mistral");
        vm.IsGeminiSelected.Should().BeTrue();
        vm.IsOllamaSelected.Should().BeFalse();
    }

    [WpfFact]
    public async Task LoadAsync_reads_all_gemini_api_keys_and_keeps_legacy_key_compatible()
    {
        var store = new FakeStore(new AppSettings
        {
            Ai = new AiSettings
            {
                Provider = AiProvider.Gemini,
                GeminiApiKey = "legacy-key",
                GeminiApiKeys = new List<string> { "legacy-key", "fallback-key" }
            }
        });
        var vm = new SettingsViewModel(store, new FakeScheduler(), NullLogger<SettingsViewModel>.Instance);

        await vm.LoadAsync();

        vm.GeminiApiKeys.Select(entry => entry.Value)
            .Should().Equal("legacy-key", "fallback-key");
        vm.GeminiApiKey.Should().Be("legacy-key");
    }

    [WpfFact]
    public async Task SaveAsync_writes_unique_non_empty_gemini_api_keys()
    {
        var store = new FakeStore(new AppSettings());
        var vm = new SettingsViewModel(store, new FakeScheduler(), NullLogger<SettingsViewModel>.Instance);
        await vm.LoadAsync();
        vm.SelectedAiProvider = AiProvider.Gemini;
        vm.GeminiApiKeys.Clear();
        vm.GeminiApiKeys.Add(new GeminiApiKeyEntryViewModel { Value = " first-key " });
        vm.GeminiApiKeys.Add(new GeminiApiKeyEntryViewModel { Value = "first-key" });
        vm.GeminiApiKeys.Add(new GeminiApiKeyEntryViewModel { Value = "second-key" });
        vm.GeminiApiKeys.Add(new GeminiApiKeyEntryViewModel { Value = " " });

        await vm.SaveAsync();

        store.Saved!.Ai.GeminiApiKeys.Should().Equal("first-key", "second-key");
        store.Saved.Ai.GeminiApiKey.Should().Be("first-key");
    }

    [WpfFact]
    public async Task SaveAsync_writes_ai_settings_to_store()
    {
        var store = new FakeStore(new AppSettings());
        var scheduler = new FakeScheduler();
        var vm = new SettingsViewModel(store, scheduler, NullLogger<SettingsViewModel>.Instance);
        await vm.LoadAsync();

        vm.SelectedAiProvider = AiProvider.Ollama;
        vm.SelectedAiResponseLanguage = AppLanguage.Hebrew;
        vm.OllamaBaseUrl = "http://localhost:11434";
        vm.OllamaModel = "llama3.1";

        await vm.SaveAsync();

        store.Saved.Should().NotBeNull();
        store.Saved!.Ai.Provider.Should().Be(AiProvider.Ollama);
        store.Saved.Ai.ResponseLanguage.Should().Be(AppLanguage.Hebrew);
        store.Saved.Ai.OllamaBaseUrl.Should().Be("http://localhost:11434");
        store.Saved.Ai.OllamaModel.Should().Be("llama3.1");
    }

    [WpfFact]
    public async Task SaveAsync_falls_back_to_defaults_for_blank_ai_fields()
    {
        var store = new FakeStore(new AppSettings());
        var scheduler = new FakeScheduler();
        var vm = new SettingsViewModel(store, scheduler, NullLogger<SettingsViewModel>.Instance);
        await vm.LoadAsync();

        vm.SelectedAiProvider = AiProvider.Gemini;
        vm.GeminiModel = "   ";
        vm.OllamaBaseUrl = "";
        vm.OllamaModel = "";

        await vm.SaveAsync();

        store.Saved!.Ai.GeminiModel.Should().Be("gemini-2.5-flash");
        store.Saved.Ai.OllamaBaseUrl.Should().Be("http://localhost:11434");
        store.Saved.Ai.OllamaModel.Should().Be("llama3.1");
    }

    [WpfFact]
    public async Task SaveAsync_reports_failure_when_scheduler_fails()
    {
        var store = new FakeStore(new AppSettings());
        var scheduler = new FakeScheduler { Failure = ResultError.From("SCHEDULE_FAILED", "denied") };
        var vm = new SettingsViewModel(store, scheduler, NullLogger<SettingsViewModel>.Instance);

        await vm.SaveAsync();

        vm.StatusText.Should().Contain("schedule update failed");
        store.Saved.Should().NotBeNull();
    }

    [WpfFact]
    public async Task LoadAsync_reads_auto_update_toggles_from_store()
    {
        var store = new FakeStore(new AppSettings
        {
            Updater = new UpdaterSettings { CheckOnStartup = true, AutoApply = true }
        });
        var vm = new SettingsViewModel(store, new FakeScheduler(), NullLogger<SettingsViewModel>.Instance);

        await vm.LoadAsync();

        vm.CheckForUpdatesOnStartup.Should().BeTrue();
        vm.AutoInstallAppUpdates.Should().BeTrue();
    }

    [WpfFact]
    public async Task SaveAsync_persists_auto_update_toggles_without_wiping_feed_settings()
    {
        var store = new FakeStore(new AppSettings
        {
            Updater = new UpdaterSettings
            {
                GitHubRepoUrl = "https://github.com/YossiYad/driverUpdater",
                AllowPrerelease = true
            }
        });
        var vm = new SettingsViewModel(store, new FakeScheduler(), NullLogger<SettingsViewModel>.Instance);
        await vm.LoadAsync();

        vm.CheckForUpdatesOnStartup = true;
        vm.AutoInstallAppUpdates = true;
        await vm.SaveAsync();

        store.Saved.Should().NotBeNull();
        store.Saved!.Updater.CheckOnStartup.Should().BeTrue();
        store.Saved.Updater.AutoApply.Should().BeTrue();
        store.Saved.Updater.GitHubRepoUrl.Should().Be("https://github.com/YossiYad/driverUpdater");
        store.Saved.Updater.AllowPrerelease.Should().BeTrue("feed settings without a UI must be preserved on save");
    }

    [WpfFact]
    public async Task SaveAsync_preserves_the_welcome_guide_marker()
    {
        var store = new FakeStore(new AppSettings
        {
            Onboarding = new OnboardingSettings { LastShownVersion = WelcomeExperience.CurrentVersion }
        });
        var vm = new SettingsViewModel(store, new FakeScheduler(), NullLogger<SettingsViewModel>.Instance);
        await vm.LoadAsync();

        await vm.SaveAsync();

        store.Saved.Should().NotBeNull();
        store.Saved!.Onboarding.LastShownVersion.Should().Be(WelcomeExperience.CurrentVersion);
    }

    [WpfFact]
    public void CheckForUpdates_is_unavailable_without_an_updater()
    {
        var vm = new SettingsViewModel(new FakeStore(new AppSettings()), new FakeScheduler(),
            NullLogger<SettingsViewModel>.Instance);

        vm.CanCheckForUpdates.Should().BeFalse();
        vm.CheckForUpdatesCommand.CanExecute(null).Should().BeFalse();
    }

    [WpfFact]
    public async Task CheckForUpdates_reports_latest_when_none_available()
    {
        var updater = new FakeAppUpdater(AppUpdateCheckResult.None);
        var vm = new SettingsViewModel(new FakeStore(new AppSettings()), new FakeScheduler(),
            NullLogger<SettingsViewModel>.Instance, appUpdater: updater);

        await vm.CheckForUpdatesCommand.ExecuteAsync(null);

        updater.CheckCalls.Should().Be(1);
        updater.DownloadCalls.Should().Be(0);
        vm.CanCheckForUpdates.Should().BeTrue();
        vm.StatusText.Should().Be("You're on the latest version.");
    }

    [WpfFact]
    public async Task CheckForUpdates_downloads_when_update_available_and_user_accepts()
    {
        var updater = new FakeAppUpdater(AppUpdateCheckResult.Available("1.2.3"));
        var prompt = new FakeAppUpdatePrompt(answer: true);
        var vm = new SettingsViewModel(new FakeStore(new AppSettings()), new FakeScheduler(),
            NullLogger<SettingsViewModel>.Instance, appUpdater: updater, appUpdatePrompt: prompt);

        await vm.CheckForUpdatesCommand.ExecuteAsync(null);

        prompt.Prompts.Should().ContainSingle().Which.Should().Be("1.2.3");
        updater.DownloadCalls.Should().Be(1);
    }

    [WpfFact]
    public async Task CheckForUpdates_does_not_download_when_user_declines()
    {
        var updater = new FakeAppUpdater(AppUpdateCheckResult.Available("1.2.3"));
        var prompt = new FakeAppUpdatePrompt(answer: false);
        var vm = new SettingsViewModel(new FakeStore(new AppSettings()), new FakeScheduler(),
            NullLogger<SettingsViewModel>.Instance, appUpdater: updater, appUpdatePrompt: prompt);

        await vm.CheckForUpdatesCommand.ExecuteAsync(null);

        updater.DownloadCalls.Should().Be(0);
        vm.StatusText.Should().Contain("1.2.3");
    }

    [WpfFact]
    public async Task CheckForUpdates_explains_when_portable_install_cannot_self_update()
    {
        var updater = new FakeAppUpdater(AppUpdateCheckResult.NotInstalled);
        var vm = new SettingsViewModel(new FakeStore(new AppSettings()), new FakeScheduler(),
            NullLogger<SettingsViewModel>.Instance, appUpdater: updater);

        await vm.CheckForUpdatesCommand.ExecuteAsync(null);

        vm.StatusText.Should().Contain("Setup installer");
        vm.StatusText.Should().Contain("GitHub");
    }

    [WpfFact]
    public async Task CheckForUpdates_does_not_report_latest_when_check_failed()
    {
        var updater = new FakeAppUpdater(AppUpdateCheckResult.Failed);
        var vm = new SettingsViewModel(new FakeStore(new AppSettings()), new FakeScheduler(),
            NullLogger<SettingsViewModel>.Instance, appUpdater: updater);

        await vm.CheckForUpdatesCommand.ExecuteAsync(null);

        vm.StatusText.Should().Be("Could not check for updates. See logs for details.");
    }

    [WpfFact]
    public async Task ClearDriverCacheAsync_clears_cached_results_and_updates_status()
    {
        var cache = new FakeDriverCacheStore(removedUpdateCount: 3);
        var vm = new SettingsViewModel(
            new FakeStore(new AppSettings()),
            new FakeScheduler(),
            NullLogger<SettingsViewModel>.Instance,
            driverCacheStore: cache);

        vm.ClearDriverCacheCommand.CanExecute(null).Should().BeTrue();
        await vm.ClearDriverCacheCommand.ExecuteAsync(null);

        cache.ClearCalls.Should().Be(1);
        vm.StatusText.Should().Contain("Removed 3 cached update result(s)");
    }

    private sealed class FakeAppUpdater : IAppUpdater
    {
        private readonly AppUpdateCheckResult _result;
        public int CheckCalls { get; private set; }
        public int DownloadCalls { get; private set; }

        public FakeAppUpdater(AppUpdateCheckResult result) { _result = result; }

        public Task CheckAndApplyAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<AppUpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
        {
            CheckCalls++;
            return Task.FromResult(_result);
        }

        public Task DownloadAndApplyAsync(IProgress<int>? progress = null, CancellationToken cancellationToken = default)
        {
            DownloadCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAppUpdatePrompt : IAppUpdatePrompt
    {
        private readonly bool _answer;
        public List<string?> Prompts { get; } = new();

        public FakeAppUpdatePrompt(bool answer) { _answer = answer; }

        public bool Confirm(string? version)
        {
            Prompts.Add(version);
            return _answer;
        }
    }

    private sealed class FakeStore : ISettingsStore
    {
        private readonly AppSettings _initial;
        public AppSettings? Saved { get; private set; }

        public FakeStore(AppSettings initial) { _initial = initial; }
        public string SettingsPath => @"C:\Temp\settings.json";
        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(_initial);
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            Saved = settings;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeDriverCacheStore : IDriverCacheStore
    {
        private readonly int _removedUpdateCount;

        public FakeDriverCacheStore(int removedUpdateCount)
        {
            _removedUpdateCount = removedUpdateCount;
        }

        public event EventHandler? Cleared;
        public int ClearCalls { get; private set; }

        public Task<DriverCacheSnapshot?> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<DriverCacheSnapshot?>(null);

        public Task SaveAsync(
            DriverCacheSnapshot snapshot,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<int> ClearAsync(CancellationToken cancellationToken = default)
        {
            ClearCalls++;
            Cleared?.Invoke(this, EventArgs.Empty);
            return Task.FromResult(_removedUpdateCount);
        }
    }

    private sealed class FakeApplicationStartupService : IApplicationStartupService
    {
        public int ApplyCalls { get; private set; }
        public bool StartWithWindows { get; private set; }
        public bool StartMinimized { get; private set; }

        public Task ApplyAsync(
            bool startWithWindows,
            bool startMinimized,
            CancellationToken cancellationToken = default)
        {
            ApplyCalls++;
            StartWithWindows = startWithWindows;
            StartMinimized = startMinimized;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeScheduler : ISchedulerService
    {
        public int ApplyCalls { get; private set; }
        public ResultError? Failure { get; set; }

        public Task<Result<ScheduledTaskInfo?>> ApplyAsync(ScheduleMode mode, ScheduleCadence cadence, TimeOnly timeOfDay, DayOfWeek dayOfWeek, CancellationToken cancellationToken = default)
        {
            ApplyCalls++;
            if (Failure is not null)
            {
                return Task.FromResult(Result<ScheduledTaskInfo?>.Failure(Failure));
            }
            return Task.FromResult(Result<ScheduledTaskInfo?>.Success(null));
        }

        public Task<ScheduledTaskInfo?> GetCurrentAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<ScheduledTaskInfo?>(null);

        public Task RemoveAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeLogCleanupService : ILogCleanupService
    {
        public string LogDirectory => @"C:\ProgramData\DriverUpdater\Logs";
        public int DeletedFiles { get; init; }
        public LogCleanupSettings? LastSettings { get; private set; }

        public Task<int> CleanupAsync(
            LogCleanupSettings settings,
            CancellationToken cancellationToken = default)
        {
            LastSettings = settings;
            return Task.FromResult(DeletedFiles);
        }
    }
}
