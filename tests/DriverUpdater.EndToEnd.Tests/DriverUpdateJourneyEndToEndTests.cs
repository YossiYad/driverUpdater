using System.Net.Http;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using DriverUpdater.Core.Options;
using DriverUpdater.EndToEnd.Tests.Harness;
using DriverUpdater.Infrastructure.History;
using DriverUpdater.Services.Backup;
using DriverUpdater.Services.Install;
using DriverUpdater.Services.Scanning;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DriverUpdater.EndToEnd.Tests;

/// <summary>
/// Walks the complete user journey with production components: WMI rows are projected by the
/// real <see cref="DriverScanService"/>, a real update source yields a candidate, the real
/// <see cref="InstallPipeline"/> installs it (restore point, backup, install, read-back
/// verification), and the real <see cref="SqliteHistoryRepository"/> persists the result to a
/// real SQLite file. Only the four OS boundaries (WMI, restore point, pnputil, driver read-back)
/// are faked.
/// </summary>
public sealed class DriverUpdateJourneyEndToEndTests : IAsyncLifetime
{
    private readonly TempWorkspace _workspace = new();
    private SqliteHistoryRepository _history = null!;

    private const string NetworkDeviceId = @"PCI\VEN_8086&DEV_15F3&SUBSYS_00008086&REV_11\3&11583659&0&FE";

    public async Task InitializeAsync()
    {
        _history = new SqliteHistoryRepository(
            new StaticOptionsMonitor<HistorySettings>(new HistorySettings
            {
                DatabasePath = _workspace.Path("history.db")
            }),
            NullLogger<SqliteHistoryRepository>.Instance);
        await _history.InitializeAsync();
    }

    public Task DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _workspace.Dispose();
        return Task.CompletedTask;
    }

    private static FakeWmiQueryRunner OneOutdatedNetworkCard() =>
        new FakeWmiQueryRunner().WithSignedDrivers(
            FakeWmiQueryRunner.SignedDriverRow(
                deviceId: NetworkDeviceId,
                deviceName: "Intel(R) Ethernet Connection I219-V",
                driverVersion: "12.18.9.23",
                driverDateDmtf: FakeWmiQueryRunner.Dmtf(2021, 3, 15),
                providerName: "Intel",
                deviceClass: "NET",
                manufacturer: "Intel Corporation",
                infName: "oem42.inf",
                hardwareIds: new[] { @"PCI\VEN_8086&DEV_15F3&SUBSYS_00008086&REV_11" }),
            FakeWmiQueryRunner.SignedDriverRow(
                deviceId: @"ROOT\SYSTEM\0000",
                deviceName: "Microsoft Print to PDF",
                driverVersion: "10.0.26100.1",
                driverDateDmtf: FakeWmiQueryRunner.Dmtf(2006, 6, 21),
                providerName: "Microsoft",
                deviceClass: "PRINTER"));

    private static UpdateCandidate NewerNetworkDriver() => new(
        ForHardwareId: @"PCI\VEN_8086&DEV_15F3&SUBSYS_00008086&REV_11",
        Source: UpdateSource.MicrosoftCatalog,
        NewVersion: new Version(12, 19, 2, 15),
        NewDate: new DateOnly(2024, 6, 1),
        DownloadUrl: new Uri("https://catalog.update.microsoft.com/download/intel-net.cab"),
        SizeBytes: 2_400_000,
        KbArticle: null,
        IsSuperseded: false,
        SourceUpdateId: "catalog-intel-net-12.19.2.15",
        SupersededIds: Array.Empty<string>(),
        InstallKind: UpdateInstallKind.PnPUtilPackage);

    private InstallPipeline BuildPipeline(
        IPnPUtilRunner pnputil,
        IInstalledDriverProbe probe,
        IRestorePointService restorePoints,
        IHttpClientFactory httpClientFactory)
    {
        var backupService = new BackupService(
            pnputil,
            new StaticOptionsMonitor<BackupSettings>(new BackupSettings { RootPath = _workspace.Path("Backups") }),
            NullLogger<BackupService>.Instance);

        return new InstallPipeline(
            restorePoints,
            backupService,
            new FakeWuApiClient(),
            NullLogger<InstallPipeline>.Instance,
            pnputil: pnputil,
            powerShell: new FakeExpandPowerShellInvoker(),
            vendorInstallerRunner: null,
            httpClientFactory: httpClientFactory,
            historyRepository: _history,
            clock: null,
            vendorPageResolver: null,
            installedDriverProbe: probe);
    }

    [Fact]
    public async Task Scan_match_install_and_verify_records_a_successful_operation_in_history()
    {
        var scanner = new DriverScanService(OneOutdatedNetworkCard(), NullLogger<DriverScanService>.Instance);
        var drivers = await scanner.ScanAsync().ToListAsync();
        drivers.Should().HaveCount(2);

        var networkCard = drivers.Single(d => d.DeviceClass == "NET");
        var candidate = NewerNetworkDriver();
        candidate.IsNewerThan(networkCard).Should().BeTrue();

        var pnputil = new FakePnPUtilRunner();
        var probe = new ScriptedInstalledDriverProbe()
            .Always(networkCard.DeviceId, new InstalledDriverState(new Version(12, 19, 2, 15), new DateOnly(2024, 6, 1)));
        var restorePoints = new FakeRestorePointService();
        var downloads = new StubHttpClientFactory(CabPayload());
        var pipeline = BuildPipeline(pnputil, probe, restorePoints, downloads);

        var operation = UpdateOperation.NewPending(candidate, networkCard);
        var progressReports = new List<UpdateStatus>();
        var finished = await pipeline.ExecuteAsync(
            operation,
            new InstallOptions(CreateRestorePoint: true, BackupCurrentDriver: true, DryRun: false),
            new Progress<UpdateOperation>(report => progressReports.Add(report.Status)));

        finished.Status.Should().Be(UpdateStatus.Succeeded);
        restorePoints.Descriptions.Should().ContainSingle()
            .Which.Should().Contain("Intel(R) Ethernet Connection I219-V");
        pnputil.Invocations.Should().Contain(a => a.Contains("/export-driver") && a.Contains("oem42.inf"));
        pnputil.Invocations.Should().Contain(a => a.Contains("/add-driver") && a.Contains("/install"));
        progressReports.Should().ContainInOrder(
            UpdateStatus.CreatingRestorePoint,
            UpdateStatus.BackingUp,
            UpdateStatus.Downloading,
            UpdateStatus.Installing,
            UpdateStatus.Succeeded);

        var persisted = await _history.GetOperationAsync(finished.OperationId);
        persisted.Should().NotBeNull();
        persisted!.Status.Should().Be(UpdateStatus.Succeeded);
        persisted.Candidate.SourceUpdateId.Should().Be("catalog-intel-net-12.19.2.15");
        persisted.TargetSnapshot.DeviceId.Should().Be(NetworkDeviceId);
        persisted.BackupPath.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task An_install_that_windows_silently_ignores_is_reported_as_not_applied()
    {
        var scanner = new DriverScanService(OneOutdatedNetworkCard(), NullLogger<DriverScanService>.Instance);
        var networkCard = (await scanner.ScanAsync().ToListAsync()).Single(d => d.DeviceClass == "NET");

        // pnputil reports success, but Windows keeps the higher-ranked driver already bound.
        var probe = new ScriptedInstalledDriverProbe()
            .Always(networkCard.DeviceId, new InstalledDriverState(networkCard.CurrentVersion, networkCard.CurrentDate));
        var pipeline = BuildPipeline(
            new FakePnPUtilRunner(),
            probe,
            new FakeRestorePointService(),
            new StubHttpClientFactory(CabPayload()));

        var finished = await pipeline.ExecuteAsync(
            UpdateOperation.NewPending(NewerNetworkDriver(), networkCard),
            new InstallOptions(CreateRestorePoint: false, BackupCurrentDriver: false, DryRun: false));

        finished.Status.Should().Be(UpdateStatus.Skipped);
        finished.ErrorMessage.Should().Contain("kept the existing driver");

        var persisted = await _history.GetOperationAsync(finished.OperationId);
        persisted!.Status.Should().Be(UpdateStatus.Skipped);
    }

    [Fact]
    public async Task A_reboot_required_install_defers_verification_and_stays_successful()
    {
        var scanner = new DriverScanService(OneOutdatedNetworkCard(), NullLogger<DriverScanService>.Instance);
        var networkCard = (await scanner.ScanAsync().ToListAsync()).Single(d => d.DeviceClass == "NET");

        // The new driver only binds after a restart, so the read-back still shows the old one.
        var probe = new ScriptedInstalledDriverProbe()
            .Always(networkCard.DeviceId, new InstalledDriverState(networkCard.CurrentVersion, networkCard.CurrentDate));
        var pipeline = BuildPipeline(
            new FakePnPUtilRunner(_ => new ProcessResult(3010, "System reboot is needed.", string.Empty)),
            probe,
            new FakeRestorePointService(),
            new StubHttpClientFactory(CabPayload()));

        var finished = await pipeline.ExecuteAsync(
            UpdateOperation.NewPending(NewerNetworkDriver(), networkCard),
            new InstallOptions(CreateRestorePoint: false, BackupCurrentDriver: false, DryRun: false));

        finished.Status.Should().Be(UpdateStatus.Succeeded);
        finished.ErrorMessage.Should().Contain("Reboot required");
        probe.ProbedDeviceIds.Should().BeEmpty("a read-back before the restart would falsely report 'unchanged'");
    }

    [Fact]
    public async Task A_dry_run_touches_nothing_on_the_machine()
    {
        var scanner = new DriverScanService(OneOutdatedNetworkCard(), NullLogger<DriverScanService>.Instance);
        var networkCard = (await scanner.ScanAsync().ToListAsync()).Single(d => d.DeviceClass == "NET");

        var pnputil = new FakePnPUtilRunner();
        var restorePoints = new FakeRestorePointService();
        var pipeline = BuildPipeline(
            pnputil,
            new ScriptedInstalledDriverProbe(),
            restorePoints,
            new StubHttpClientFactory(CabPayload()));

        var finished = await pipeline.ExecuteAsync(
            UpdateOperation.NewPending(NewerNetworkDriver(), networkCard),
            new InstallOptions(CreateRestorePoint: true, BackupCurrentDriver: true, DryRun: true));

        finished.Status.Should().Be(UpdateStatus.Skipped);
        finished.ErrorMessage.Should().Contain("Create system restore point");
        pnputil.Invocations.Should().BeEmpty();
        restorePoints.Descriptions.Should().BeEmpty();
    }

    [Fact]
    public async Task A_batch_of_installs_reuses_one_restore_point_and_keeps_going_after_a_failure()
    {
        var scanner = new DriverScanService(
            new FakeWmiQueryRunner().WithSignedDrivers(
                FakeWmiQueryRunner.SignedDriverRow(
                    @"PCI\VEN_8086&DEV_0001\1", "Device One", "1.0.0.0",
                    FakeWmiQueryRunner.Dmtf(2020, 1, 1), "Intel", "NET", infName: "one.inf"),
                FakeWmiQueryRunner.SignedDriverRow(
                    @"PCI\VEN_8086&DEV_0002\1", "Device Two", "1.0.0.0",
                    FakeWmiQueryRunner.Dmtf(2020, 1, 1), "Intel", "NET", infName: "two.inf")),
            NullLogger<DriverScanService>.Instance);
        var drivers = await scanner.ScanAsync().ToListAsync();

        var restorePoints = new FakeRestorePointService();
        var probe = new ScriptedInstalledDriverProbe();
        foreach (var driver in drivers)
        {
            probe.Always(driver.DeviceId, new InstalledDriverState(new Version(2, 0, 0, 0), new DateOnly(2024, 1, 1)));
        }

        var installAttempts = 0;
        var pnputil = new FakePnPUtilRunner(arguments =>
            arguments.Contains("/add-driver") && ++installAttempts == 2
                ? new ProcessResult(87, string.Empty, "The parameter is incorrect.")
                : new ProcessResult(0, "ok", string.Empty));
        var pipeline = BuildPipeline(pnputil, probe, restorePoints, new StubHttpClientFactory(CabPayload()));

        var results = new List<UpdateOperation>();
        foreach (var driver in drivers)
        {
            var candidate = NewerNetworkDriver() with
            {
                ForHardwareId = driver.HardwareId,
                SourceUpdateId = "catalog-" + driver.DeviceId,
                DownloadUrl = new Uri($"https://catalog.update.microsoft.com/download/{driver.InfName}.cab")
            };
            results.Add(await pipeline.ExecuteAsync(
                UpdateOperation.NewPending(candidate, driver),
                new InstallOptions(CreateRestorePoint: true, BackupCurrentDriver: false, DryRun: false)));
        }

        restorePoints.Descriptions.Should().HaveCount(1, "one batch needs one rollback anchor, not one per driver");
        results[0].Status.Should().Be(UpdateStatus.Succeeded);
        results[1].Status.Should().Be(UpdateStatus.Failed);
        results.Should().OnlyContain(r => r.RestorePointSequenceNumber == "1");

        var history = await _history.ListOperationsAsync();
        history.Select(o => o.OperationId).Should().Contain(results.Select(r => r.OperationId));
    }

    // A minimal, valid .cab-shaped payload: the pipeline only needs a file to hand to pnputil.
    private static byte[] CabPayload() => "MSCF\0\0\0\0driver-package"u8.ToArray();
}
