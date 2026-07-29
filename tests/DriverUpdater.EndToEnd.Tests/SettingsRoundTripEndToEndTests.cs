using System.IO;
using System.Text.Json;
using DriverUpdater.App.ViewModels;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using DriverUpdater.Core.Options;
using DriverUpdater.Core.Results;
using DriverUpdater.EndToEnd.Tests.Harness;
using DriverUpdater.Infrastructure.Settings;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriverUpdater.EndToEnd.Tests;

/// <summary>
/// Exercises the whole settings path the user drives: an existing settings.json on disk is read
/// by the real <see cref="JsonSettingsStore"/>, projected into the real
/// <see cref="SettingsViewModel"/>, edited, saved back through the store, and read again from
/// disk. Nothing here is mocked except the Windows scheduler boundary.
/// </summary>
public sealed class SettingsRoundTripEndToEndTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    private (SettingsViewModel ViewModel, JsonSettingsStore Store, string Path) BuildApp()
    {
        var path = _workspace.Path("settings.json");
        var store = new JsonSettingsStore(NullLogger<JsonSettingsStore>.Instance, path);
        var viewModel = new SettingsViewModel(
            store,
            new RecordingSchedulerService(),
            NullLogger<SettingsViewModel>.Instance);
        return (viewModel, store, path);
    }

    private static async Task WriteSettingsAsync(string path, AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
    }

    [Fact]
    public async Task Editing_and_saving_settings_persists_the_edited_values_to_disk()
    {
        var (viewModel, store, path) = BuildApp();
        await WriteSettingsAsync(path, new AppSettings());

        await viewModel.LoadAsync();
        viewModel.EnableWindowsUpdate = false;
        viewModel.BackupRetentionDays = 45;
        viewModel.ScheduleMode = ScheduleMode.ScanOnly;
        viewModel.LogRetentionDays = 21;
        await viewModel.SaveAsync();

        var reloaded = await store.LoadAsync();
        reloaded.Updater.WindowsUpdateEnabled.Should().BeFalse();
        reloaded.Backup.RetentionDays.Should().Be(45);
        reloaded.Schedule.Mode.Should().Be(ScheduleMode.ScanOnly);
        reloaded.LogCleanup.RetentionDays.Should().Be(21);
    }

    [Fact]
    public async Task Saving_settings_keeps_the_custom_backup_folder_configured_on_disk()
    {
        var (viewModel, store, path) = BuildApp();
        var customBackupRoot = _workspace.Path("MyDriverBackups");
        await WriteSettingsAsync(path, new AppSettings
        {
            Backup = new BackupSettings { RootPath = customBackupRoot, RetentionDays = 30 }
        });

        await viewModel.LoadAsync();
        viewModel.BackupRetentionDays = 60;
        await viewModel.SaveAsync();

        var reloaded = await store.LoadAsync();
        reloaded.Backup.RetentionDays.Should().Be(60);
        reloaded.Backup.RootPath.Should().Be(
            customBackupRoot,
            "the Settings window has no backup-folder field, so saving it must not erase the configured folder");
    }

    [Fact]
    public async Task Saving_settings_keeps_the_custom_history_database_configured_on_disk()
    {
        var (viewModel, store, path) = BuildApp();
        var customDatabase = _workspace.Path("history", "installs.db");
        await WriteSettingsAsync(path, new AppSettings
        {
            History = new HistorySettings { DatabasePath = customDatabase }
        });

        await viewModel.LoadAsync();
        viewModel.EnableOemHints = false;
        await viewModel.SaveAsync();

        var reloaded = await store.LoadAsync();
        reloaded.Updater.OemSourcesEnabled.Should().BeFalse();
        reloaded.History.DatabasePath.Should().Be(
            customDatabase,
            "the Settings window has no history-database field, so saving it must not erase the configured path");
    }

    [Fact]
    public async Task Saving_settings_keeps_the_app_update_feed_and_onboarding_state()
    {
        var (viewModel, store, path) = BuildApp();
        await WriteSettingsAsync(path, new AppSettings
        {
            Updater = new UpdaterSettings
            {
                GitHubRepoUrl = "https://github.com/example/custom",
                FeedUrl = "https://example.invalid/feed",
                AllowPrerelease = true
            },
            Onboarding = new OnboardingSettings { LastShownVersion = "0.1.30", ShowOnStartup = true }
        });

        await viewModel.LoadAsync();
        viewModel.CheckForUpdatesOnStartup = true;
        await viewModel.SaveAsync();

        var reloaded = await store.LoadAsync();
        reloaded.Updater.CheckOnStartup.Should().BeTrue();
        reloaded.Updater.GitHubRepoUrl.Should().Be("https://github.com/example/custom");
        reloaded.Updater.FeedUrl.Should().Be("https://example.invalid/feed");
        reloaded.Updater.AllowPrerelease.Should().BeTrue();
        reloaded.Onboarding.LastShownVersion.Should().Be("0.1.30");
    }

    [Fact]
    public async Task A_fresh_install_saves_an_ai_model_the_gemini_endpoint_accepts()
    {
        var (viewModel, store, path) = BuildApp();
        await WriteSettingsAsync(path, new AppSettings());

        // A brand-new user opens Settings, enables Gemini, and saves without touching the model box.
        await viewModel.LoadAsync();
        viewModel.SelectedAiProvider = AiProvider.Gemini;
        viewModel.GeminiApiKey = "test-key";
        await viewModel.SaveAsync();

        var reloaded = await store.LoadAsync();
        reloaded.Ai.GeminiModel.Should().Be(
            new AiSettings().GeminiModel,
            "the Settings default must match the shipped AiSettings default, or the saved model does not exist at the API");
    }

    [Fact]
    public async Task Multiple_gemini_api_keys_survive_a_save_and_reload_cycle()
    {
        var (viewModel, store, path) = BuildApp();
        await WriteSettingsAsync(path, new AppSettings());

        await viewModel.LoadAsync();
        viewModel.SelectedAiProvider = AiProvider.Gemini;
        viewModel.GeminiApiKey = "primary-key";
        viewModel.AddGeminiApiKeyCommand.Execute(null);
        viewModel.GeminiApiKeys[1].Value = "secondary-key";
        await viewModel.SaveAsync();

        var reloaded = await store.LoadAsync();
        reloaded.Ai.GetGeminiApiKeys().Should().Equal("primary-key", "secondary-key");

        var secondSession = new SettingsViewModel(
            store,
            new RecordingSchedulerService(),
            NullLogger<SettingsViewModel>.Instance);
        await secondSession.LoadAsync();
        secondSession.GeminiApiKeys.Select(entry => entry.Value)
            .Should().Equal("primary-key", "secondary-key");
    }

    [Fact]
    public async Task A_corrupt_settings_file_falls_back_to_defaults_instead_of_crashing()
    {
        var (viewModel, store, path) = BuildApp();
        await File.WriteAllTextAsync(path, "{ this is not json");

        await viewModel.LoadAsync();

        viewModel.EnableWindowsUpdate.Should().BeTrue();
        var loaded = await store.LoadAsync();
        loaded.Should().BeEquivalentTo(new AppSettings());
    }

    private sealed class RecordingSchedulerService : ISchedulerService
    {
        public List<ScheduleMode> AppliedModes { get; } = new();

        public Task<Result<ScheduledTaskInfo?>> ApplyAsync(
            ScheduleMode mode,
            ScheduleCadence cadence,
            TimeOnly timeOfDay,
            DayOfWeek dayOfWeek,
            CancellationToken cancellationToken = default)
        {
            AppliedModes.Add(mode);
            return Task.FromResult(Result<ScheduledTaskInfo?>.Success(null));
        }

        public Task<ScheduledTaskInfo?> GetCurrentAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<ScheduledTaskInfo?>(null);

        public Task RemoveAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
