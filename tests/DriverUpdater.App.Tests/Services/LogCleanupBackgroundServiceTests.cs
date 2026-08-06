using System.IO;
using DriverUpdater.App.Services;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using DriverUpdater.Core.Options;
using DriverUpdater.Core.Results;
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

    [Fact]
    public async Task StartAsync_purges_backups_using_the_saved_retention_period()
    {
        var settings = new AppSettings
        {
            Backup = new BackupSettings { RetentionDays = 45 }
        };
        var backup = new RecordingBackupService();
        var service = new LogCleanupBackgroundService(
            new FakeSettingsStore(settings),
            new RecordingCleanupService(),
            NullLogger<LogCleanupBackgroundService>.Instance,
            backup);

        await service.StartAsync(CancellationToken.None);
        var completed = await Task.WhenAny(backup.Called.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        await service.StopAsync(CancellationToken.None);

        completed.Should().Be(backup.Called.Task);
        (await backup.Called.Task).Should().Be(TimeSpan.FromDays(45));
    }

    [Fact]
    public async Task StartAsync_still_purges_backups_when_log_cleanup_fails()
    {
        var backup = new RecordingBackupService();
        var service = new LogCleanupBackgroundService(
            new FakeSettingsStore(new AppSettings()),
            new RecordingCleanupService { ThrowOnCall = true },
            NullLogger<LogCleanupBackgroundService>.Instance,
            backup);

        await service.StartAsync(CancellationToken.None);
        var completed = await Task.WhenAny(backup.Called.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        await service.StopAsync(CancellationToken.None);

        completed.Should().Be(backup.Called.Task);
    }

    private sealed class RecordingCleanupService : ILogCleanupService
    {
        public string LogDirectory => "test";
        public bool ThrowOnCall { get; init; }
        public TaskCompletionSource<LogCleanupSettings> Called { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<int> CleanupAsync(
            LogCleanupSettings settings,
            CancellationToken cancellationToken = default)
        {
            if (ThrowOnCall)
            {
                throw new IOException("log file is locked");
            }
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

    private sealed class RecordingBackupService : IBackupService
    {
        public TaskCompletionSource<TimeSpan> Called { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int PurgeBackupsOlderThan(TimeSpan age)
        {
            Called.TrySetResult(age);
            return 0;
        }

        public Task<Result<BackupArtifact>> BackupDriverAsync(
            DriverInfo driver,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Result<bool>> RestoreFromBackupAsync(
            BackupArtifact artifact,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IReadOnlyList<BackupArtifact> ListBackups() => Array.Empty<BackupArtifact>();
    }
}
