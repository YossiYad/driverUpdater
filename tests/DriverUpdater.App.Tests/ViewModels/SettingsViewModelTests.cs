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
        vm.GeminiApiKey.Should().Be("abc123");
        vm.GeminiModel.Should().Be("gemini-2.0-flash");
        vm.EnableAiWebSearch.Should().BeFalse();
        vm.OllamaBaseUrl.Should().Be("http://host:1234");
        vm.OllamaModel.Should().Be("mistral");
        vm.IsGeminiSelected.Should().BeTrue();
        vm.IsOllamaSelected.Should().BeFalse();
    }

    [WpfFact]
    public async Task SaveAsync_writes_ai_settings_to_store()
    {
        var store = new FakeStore(new AppSettings());
        var scheduler = new FakeScheduler();
        var vm = new SettingsViewModel(store, scheduler, NullLogger<SettingsViewModel>.Instance);
        await vm.LoadAsync();

        vm.SelectedAiProvider = AiProvider.Ollama;
        vm.OllamaBaseUrl = "http://localhost:11434";
        vm.OllamaModel = "llama3.1";

        await vm.SaveAsync();

        store.Saved.Should().NotBeNull();
        store.Saved!.Ai.Provider.Should().Be(AiProvider.Ollama);
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
}
