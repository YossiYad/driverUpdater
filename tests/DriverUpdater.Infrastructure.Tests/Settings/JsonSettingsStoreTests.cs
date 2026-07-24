using DriverUpdater.Core.Models;
using DriverUpdater.Core.Options;
using DriverUpdater.Infrastructure.Settings;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriverUpdater.Infrastructure.Tests.Settings;

public class JsonSettingsStoreTests : IDisposable
{
    private readonly string _path;

    public JsonSettingsStoreTests()
    {
        _path = Path.Combine(Path.GetTempPath(), "DriverUpdaterSettingsTests", Guid.NewGuid().ToString("N"), "settings.json");
    }

    public void Dispose()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch
        {
        }
    }

    [Fact]
    public async Task LoadAsync_returns_defaults_when_file_missing()
    {
        var store = NewStore();

        var settings = await store.LoadAsync();

        settings.Should().NotBeNull();
        settings.Application.CloseBehavior.Should().Be(WindowCloseBehavior.ExitApplication);
        settings.Application.StartWithWindows.Should().BeFalse();
        settings.Catalog.Enabled.Should().BeTrue();
        settings.Schedule.Mode.Should().Be(ScheduleMode.Manual);
    }

    [Fact]
    public async Task SaveAsync_then_LoadAsync_roundtrips_settings()
    {
        var store = NewStore();
        var settings = new AppSettings
        {
            Application = new ApplicationSettings
            {
                CloseBehavior = WindowCloseBehavior.KeepRunningInBackground,
                StartWithWindows = true,
                StartMinimized = true
            },
            Catalog = new CatalogSettings { Enabled = true, MaxConcurrentSearches = 8, CacheDuration = TimeSpan.FromHours(48) },
            Backup = new BackupSettings { RetentionDays = 60 },
            Ai = new AiSettings
            {
                Provider = AiProvider.Gemini,
                GeminiApiKey = "first-key",
                GeminiApiKeys = new List<string> { "first-key", "second-key" },
                GeminiDailyRequestLimit = 500,
                ShowAiScanUsageWarning = false
            },
            LogCleanup = new LogCleanupSettings { Enabled = false, RetentionDays = 21 },
            Onboarding = new OnboardingSettings { ShowOnStartup = false },
            Schedule = new ScheduleSettings
            {
                Mode = ScheduleMode.ScanAndUpdate,
                Cadence = ScheduleCadence.Daily,
                TimeOfDay = new TimeOnly(7, 30),
                DayOfWeek = DayOfWeek.Friday
            }
        };

        await store.SaveAsync(settings);
        File.Exists(_path).Should().BeTrue();

        var loaded = await store.LoadAsync();
        loaded.Application.CloseBehavior.Should().Be(WindowCloseBehavior.KeepRunningInBackground);
        loaded.Application.StartWithWindows.Should().BeTrue();
        loaded.Application.StartMinimized.Should().BeTrue();
        loaded.Catalog.Enabled.Should().BeTrue();
        loaded.Catalog.MaxConcurrentSearches.Should().Be(8);
        loaded.Backup.RetentionDays.Should().Be(60);
        loaded.Ai.GetGeminiApiKeys().Should().Equal("first-key", "second-key");
        loaded.Ai.GeminiDailyRequestLimit.Should().Be(500);
        loaded.Ai.ShowAiScanUsageWarning.Should().BeFalse();
        loaded.LogCleanup.Enabled.Should().BeFalse();
        loaded.LogCleanup.RetentionDays.Should().Be(21);
        loaded.Onboarding.ShowOnStartup.Should().BeFalse();
        loaded.Schedule.Mode.Should().Be(ScheduleMode.ScanAndUpdate);
        loaded.Schedule.Cadence.Should().Be(ScheduleCadence.Daily);
        loaded.Schedule.TimeOfDay.Should().Be(new TimeOnly(7, 30));
        loaded.Schedule.DayOfWeek.Should().Be(DayOfWeek.Friday);
    }

    [Fact]
    public async Task LoadAsync_falls_back_to_defaults_on_malformed_json()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        await File.WriteAllTextAsync(_path, "{ not valid json");
        var store = NewStore();

        var settings = await store.LoadAsync();

        settings.Should().NotBeNull();
        settings.Catalog.Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task Legacy_onboarding_settings_enable_recurring_guide_by_default()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        await File.WriteAllTextAsync(
            _path,
            """{"Onboarding":{"LastShownVersion":"0.1.39"}}""");
        var store = NewStore();

        var settings = await store.LoadAsync();

        settings.Onboarding.ShowOnStartup.Should().BeTrue();
    }

    [Fact]
    public async Task SaveAsync_creates_parent_directories()
    {
        var store = NewStore();

        await store.SaveAsync(new AppSettings());

        File.Exists(_path).Should().BeTrue();
        Directory.Exists(Path.GetDirectoryName(_path)).Should().BeTrue();
    }

    [Fact]
    public async Task Concurrent_saves_are_serialized_and_leave_valid_json()
    {
        var store = NewStore();
        var saves = Enumerable.Range(1, 20)
            .Select(retentionDays => store.SaveAsync(new AppSettings
            {
                Backup = new BackupSettings { RetentionDays = retentionDays }
            }));

        await Task.WhenAll(saves);

        var loaded = await store.LoadAsync();
        loaded.Backup.RetentionDays.Should().BeInRange(1, 20);
    }

    private JsonSettingsStore NewStore() =>
        new(NullLogger<JsonSettingsStore>.Instance, _path);
}
