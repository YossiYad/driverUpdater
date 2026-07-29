using System.IO;
using DriverUpdater.App.Services;
using DriverUpdater.Core.Models;
using DriverUpdater.Core.Options;
using DriverUpdater.Core.Results;
using DriverUpdater.EndToEnd.Tests.Harness;
using DriverUpdater.Infrastructure.History;
using DriverUpdater.Services.Backup;
using DriverUpdater.Services.Install;
using DriverUpdater.Services.Scanning;
using DriverUpdater.Services.Sources;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriverUpdater.EndToEnd.Tests;

/// <summary>
/// Two flows that touch the user's machine outside the driver grid: installing through the
/// Windows Update agent (real <see cref="WindowsUpdateSource"/> plus real
/// <see cref="InstallPipeline"/>), and the housekeeping that keeps the install history and the
/// log folder from growing without bound (real <see cref="SqliteHistoryRepository"/> and real
/// <see cref="LogCleanupService"/> over real files).
/// </summary>
public sealed class WindowsUpdateAndHousekeepingEndToEndTests : IAsyncLifetime
{
    private readonly TempWorkspace _workspace = new();
    private SqliteHistoryRepository _history = null!;

    public async Task InitializeAsync()
    {
        _history = new SqliteHistoryRepository(
            new StaticOptionsMonitor<HistorySettings>(new HistorySettings { DatabasePath = _workspace.Path("history.db") }),
            NullLogger<SqliteHistoryRepository>.Instance);
        await _history.InitializeAsync();
    }

    public Task DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _workspace.Dispose();
        return Task.CompletedTask;
    }

    private static readonly DriverInfo Bluetooth = new(
        DeviceId: @"USB\VID_8087&PID_0032\5&2f1c0e4&0&14",
        HardwareId: @"USB\VID_8087&PID_0032",
        DeviceName: "Intel(R) Wireless Bluetooth(R)",
        Category: DriverCategory.Bluetooth,
        Provider: "Intel",
        Manufacturer: "Intel Corporation",
        CurrentVersion: new Version(22, 100, 0, 3),
        CurrentDate: new DateOnly(2022, 6, 1),
        InfName: "oem18.inf",
        InfPath: null,
        IsSigned: true,
        DeviceClass: "BLUETOOTH");

    private static WuDriverUpdateRecord WuRecord(bool rebootRequired = false) => new(
        UpdateId: "8f1b0c62-1111-4d0a-9c2e-abcdef123456",
        RevisionNumber: 1,
        Title: "Intel - Bluetooth - 23.60.0.4",
        DriverHardwareId: @"USB\VID_8087&PID_0032",
        DriverModel: "Intel(R) Wireless Bluetooth(R)",
        DriverManufacturer: "Intel Corporation",
        DriverProvider: "Intel",
        DriverVerDate: new DateOnly(2024, 5, 20),
        MaxDownloadSize: 12_345_678,
        DownloadUrl: "https://download.windowsupdate.com/d/msdownload/update/driver/drvs/2024/05/intel-bt.cab",
        KbArticleIds: new[] { "5001111" },
        RebootBehavior: rebootRequired ? UpdateRebootBehavior.AlwaysRequired : UpdateRebootBehavior.NeverRequired);

    private InstallPipeline BuildPipeline(FakeWuApiClient wu, ScriptedInstalledDriverProbe probe)
    {
        var pnputil = new FakePnPUtilRunner();
        return new InstallPipeline(
            new FakeRestorePointService(),
            new BackupService(
                pnputil,
                new StaticOptionsMonitor<BackupSettings>(new BackupSettings { RootPath = _workspace.Path("Backups") }),
                NullLogger<BackupService>.Instance),
            wu,
            NullLogger<InstallPipeline>.Instance,
            pnputil: pnputil,
            historyRepository: _history,
            installedDriverProbe: probe);
    }

    [Fact]
    public async Task A_windows_update_driver_is_discovered_installed_through_the_agent_and_recorded()
    {
        var wu = new FakeWuApiClient(new[] { WuRecord() });
        var source = new WindowsUpdateSource(wu, NullLogger<WindowsUpdateSource>.Instance);

        var candidates = await source.SearchAsync(new[] { Bluetooth }).ToListAsync();
        candidates.Should().ContainSingle();
        var candidate = candidates[0];
        candidate.Source.Should().Be(UpdateSource.WindowsUpdate);
        candidate.InstallKind.Should().Be(UpdateInstallKind.WindowsUpdate);
        candidate.NewVersion.Should().Be(new Version(23, 60, 0, 4), "the version comes from the update title");
        candidate.KbArticle.Should().Be("KB5001111");
        candidate.IsNewerThan(Bluetooth).Should().BeTrue();

        var probe = new ScriptedInstalledDriverProbe()
            .Always(Bluetooth.DeviceId, new InstalledDriverState(new Version(23, 60, 0, 4), new DateOnly(2024, 5, 20)));
        var finished = await BuildPipeline(wu, probe).ExecuteAsync(
            UpdateOperation.NewPending(candidate, Bluetooth),
            new InstallOptions(CreateRestorePoint: false, BackupCurrentDriver: false, DryRun: false));

        finished.Status.Should().Be(UpdateStatus.Succeeded);
        wu.InstalledUpdateIds.Should().Equal("8f1b0c62-1111-4d0a-9c2e-abcdef123456");

        var persisted = await _history.GetOperationAsync(finished.OperationId);
        persisted!.Candidate.KbArticle.Should().Be("KB5001111");
    }

    [Fact]
    public async Task A_windows_update_install_that_needs_a_restart_says_so()
    {
        var wu = new FakeWuApiClient(
            new[] { WuRecord(rebootRequired: true) },
            install: _ => Result<WuInstallResult>.Success(
                new WuInstallResult(HResult: 0, RebootRequired: true, Message: "Reboot required")));
        var source = new WindowsUpdateSource(wu, NullLogger<WindowsUpdateSource>.Instance);
        var candidate = (await source.SearchAsync(new[] { Bluetooth }).ToListAsync())[0];

        var finished = await BuildPipeline(wu, new ScriptedInstalledDriverProbe()).ExecuteAsync(
            UpdateOperation.NewPending(candidate, Bluetooth),
            new InstallOptions(CreateRestorePoint: false, BackupCurrentDriver: false, DryRun: false));

        finished.Status.Should().Be(UpdateStatus.Succeeded);
        finished.ErrorMessage.Should().Contain("Reboot required");
    }

    [Fact]
    public async Task A_failing_windows_update_agent_surfaces_its_error_to_the_user()
    {
        var wu = new FakeWuApiClient(
            new[] { WuRecord() },
            install: _ => Result<WuInstallResult>.Failure("WU_INSTALL_FAILED", "0x80240022: some updates failed."));
        var source = new WindowsUpdateSource(wu, NullLogger<WindowsUpdateSource>.Instance);
        var candidate = (await source.SearchAsync(new[] { Bluetooth }).ToListAsync())[0];

        var finished = await BuildPipeline(wu, new ScriptedInstalledDriverProbe()).ExecuteAsync(
            UpdateOperation.NewPending(candidate, Bluetooth),
            new InstallOptions(CreateRestorePoint: false, BackupCurrentDriver: false, DryRun: false));

        finished.Status.Should().Be(UpdateStatus.Failed);
        finished.ErrorMessage.Should().Contain("0x80240022");
        (await _history.GetOperationAsync(finished.OperationId))!.Status.Should().Be(UpdateStatus.Failed);
    }

    [Fact]
    public async Task The_history_window_shows_the_newest_operations_first_and_honours_its_limit()
    {
        var wu = new FakeWuApiClient(new[] { WuRecord() });
        var candidate = (await new WindowsUpdateSource(wu, NullLogger<WindowsUpdateSource>.Instance)
            .SearchAsync(new[] { Bluetooth }).ToListAsync())[0];

        for (var i = 0; i < 5; i++)
        {
            var operation = UpdateOperation.NewPending(candidate, Bluetooth) with
            {
                StartedAt = new DateTimeOffset(2026, 1, 1 + i, 10, 0, 0, TimeSpan.Zero),
                Status = UpdateStatus.Succeeded,
                CompletedAt = new DateTimeOffset(2026, 1, 1 + i, 10, 5, 0, TimeSpan.Zero)
            };
            await _history.UpsertOperationAsync(operation);
        }

        var all = await _history.ListOperationsAsync();
        all.Should().HaveCount(5);
        all.Select(o => o.StartedAt).Should().BeInDescendingOrder();

        var limited = await _history.ListOperationsAsync(limit: 2);
        limited.Should().HaveCount(2);
        limited[0].StartedAt.Should().Be(new DateTimeOffset(2026, 1, 5, 10, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task Re_running_the_same_operation_updates_its_row_instead_of_duplicating_it()
    {
        var wu = new FakeWuApiClient(new[] { WuRecord() });
        var candidate = (await new WindowsUpdateSource(wu, NullLogger<WindowsUpdateSource>.Instance)
            .SearchAsync(new[] { Bluetooth }).ToListAsync())[0];
        var operation = UpdateOperation.NewPending(candidate, Bluetooth);

        await _history.UpsertOperationAsync(operation with { Status = UpdateStatus.Downloading });
        await _history.UpsertOperationAsync(operation with
        {
            Status = UpdateStatus.Succeeded,
            CompletedAt = DateTimeOffset.UtcNow
        });

        (await _history.ListOperationsAsync()).Should().ContainSingle();
        (await _history.GetOperationAsync(operation.OperationId))!.Status.Should().Be(UpdateStatus.Succeeded);
    }

    [Fact]
    public async Task Log_cleanup_removes_only_the_files_past_the_retention_window()
    {
        var logDirectory = _workspace.Path("Logs");
        Directory.CreateDirectory(logDirectory);
        var now = new DateTimeOffset(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);

        string WriteLog(string name, int ageInDays)
        {
            var path = Path.Combine(logDirectory, name);
            File.WriteAllText(path, "log contents");
            File.SetLastWriteTimeUtc(path, now.UtcDateTime.AddDays(-ageInDays));
            return path;
        }

        var fresh = WriteLog("driverupdater-20260726.log", 0);
        var almostStale = WriteLog("driverupdater-20260721.log", 6);
        var stale = WriteLog("driverupdater-20260710.log", 20);
        var unrelated = WriteLog("other-app.log", 90);

        var service = new LogCleanupService(
            NullLogger<LogCleanupService>.Instance,
            logDirectory,
            new ManualTimeProvider(now));

        var deleted = await service.CleanupAsync(new LogCleanupSettings { Enabled = true, RetentionDays = 7 });

        deleted.Should().Be(1);
        File.Exists(fresh).Should().BeTrue();
        File.Exists(almostStale).Should().BeTrue();
        File.Exists(stale).Should().BeFalse();
        File.Exists(unrelated).Should().BeTrue("cleanup must only touch this app's own log files");
    }

    [Fact]
    public async Task Log_cleanup_is_a_no_op_when_the_user_turned_it_off()
    {
        var logDirectory = _workspace.Path("Logs2");
        Directory.CreateDirectory(logDirectory);
        var path = Path.Combine(logDirectory, "driverupdater-20200101.log");
        File.WriteAllText(path, "ancient");
        File.SetLastWriteTimeUtc(path, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var service = new LogCleanupService(NullLogger<LogCleanupService>.Instance, logDirectory);

        var deleted = await service.CleanupAsync(new LogCleanupSettings { Enabled = false, RetentionDays = 1 });

        deleted.Should().Be(0);
        File.Exists(path).Should().BeTrue();
    }

    [Fact]
    public async Task An_out_of_range_retention_value_is_clamped_instead_of_deleting_everything()
    {
        var logDirectory = _workspace.Path("Logs3");
        Directory.CreateDirectory(logDirectory);
        var now = new DateTimeOffset(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);
        var today = Path.Combine(logDirectory, "driverupdater-20260726.log");
        File.WriteAllText(today, "today");
        File.SetLastWriteTimeUtc(today, now.UtcDateTime);

        var service = new LogCleanupService(
            NullLogger<LogCleanupService>.Instance,
            logDirectory,
            new ManualTimeProvider(now));

        var deleted = await service.CleanupAsync(new LogCleanupSettings { Enabled = true, RetentionDays = 0 });

        deleted.Should().Be(0);
        File.Exists(today).Should().BeTrue("retention is clamped to at least one day, so today's log survives");
    }
}
