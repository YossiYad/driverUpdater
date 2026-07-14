using System.IO;
using DriverUpdater.App.Services;
using DriverUpdater.Core.Options;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriverUpdater.App.Tests.Services;

public sealed class LogCleanupServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);
    private readonly string _logDirectory = Path.Combine(
        Path.GetTempPath(),
        "DriverUpdaterLogCleanupTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CleanupAsync_deletes_only_driverupdater_logs_older_than_retention()
    {
        Directory.CreateDirectory(_logDirectory);
        var oldLog = CreateFile("driverupdater-20260701.log", Now.AddDays(-14));
        var recentLog = CreateFile("driverupdater-20260714.log", Now.AddDays(-1));
        var unrelatedFile = CreateFile("notes.log", Now.AddDays(-30));
        var service = NewService();

        var deleted = await service.CleanupAsync(new LogCleanupSettings
        {
            Enabled = true,
            RetentionDays = 7
        });

        deleted.Should().Be(1);
        File.Exists(oldLog).Should().BeFalse();
        File.Exists(recentLog).Should().BeTrue();
        File.Exists(unrelatedFile).Should().BeTrue();
        Directory.Exists(_logDirectory).Should().BeTrue();
    }

    [Fact]
    public async Task CleanupAsync_keeps_all_logs_when_automatic_cleanup_is_disabled()
    {
        Directory.CreateDirectory(_logDirectory);
        var oldLog = CreateFile("driverupdater-20260101.log", Now.AddDays(-100));
        var service = NewService();

        var deleted = await service.CleanupAsync(new LogCleanupSettings
        {
            Enabled = false,
            RetentionDays = 7
        });

        deleted.Should().Be(0);
        File.Exists(oldLog).Should().BeTrue();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_logDirectory))
            {
                Directory.Delete(_logDirectory, recursive: true);
            }
        }
        catch
        {
        }
    }

    private LogCleanupService NewService() => new(
        NullLogger<LogCleanupService>.Instance,
        _logDirectory,
        new FixedTimeProvider(Now));

    private string CreateFile(string fileName, DateTimeOffset lastWriteTime)
    {
        var path = Path.Combine(_logDirectory, fileName);
        File.WriteAllText(path, "test");
        File.SetLastWriteTimeUtc(path, lastWriteTime.UtcDateTime);
        return path;
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
