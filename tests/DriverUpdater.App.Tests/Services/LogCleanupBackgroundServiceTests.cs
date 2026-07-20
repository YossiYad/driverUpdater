using DriverUpdater.App.Services;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Options;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriverUpdater.App.Tests.Services;

public class LogCleanupBackgroundServiceTests
{
    [Fact]
    public async Task StartAsync_runs_cleanup_automatically_with_saved_settings()
    {
        var settings = new AppSettings
        {
            LogCleanup = new LogCleanupSettings { Enabled = true, RetentionDays = 9 }
        };
        var cleanup = new RecordingCleanupService();
        var service = new LogCleanupBackgroundService(
            new FakeSettingsStore(settings),
            cleanup,
            NullLogger<LogCleanupBackgroundService>.Instance);

        await service.StartAsync(CancellationToken.None);
        var completed = await Task.WhenAny(cleanup.Called.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        await service.StopAsync(CancellationToken.None);

        completed.Should().Be(cleanup.Called.Task);
        var appliedSettings = await cleanup.Called.Task;
        appliedSettings.Enabled.Should().BeTrue();
        appliedSettings.RetentionDays.Should().Be(9);
    }

    private sealed class RecordingCleanupService : ILogCleanupService
    {
        public string LogDirectory => "test";
        public TaskCompletionSource<LogCleanupSettings> Called { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<int> CleanupAsync(
            LogCleanupSettings settings,
            CancellationToken cancellationToken = default)
        {
            Called.TrySetResult(settings);
            return Task.FromResult(0);
        }
    }

    private sealed class FakeSettingsStore(AppSettings settings) : ISettingsStore
    {
        public string SettingsPath => "settings.json";

        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(settings);

        public Task SaveAsync(
            AppSettings value,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
