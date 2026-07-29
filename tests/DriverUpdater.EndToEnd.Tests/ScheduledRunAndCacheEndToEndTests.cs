using System.IO;
using System.Net.Http;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using DriverUpdater.Core.Options;
using DriverUpdater.EndToEnd.Tests.Harness;
using DriverUpdater.Infrastructure.Cache;
using DriverUpdater.Services.Backup;
using DriverUpdater.Services.Install;
using DriverUpdater.Services.Scanning;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriverUpdater.EndToEnd.Tests;

/// <summary>
/// Runs the headless scheduled-task path exactly as <c>--scheduled-update</c> does: the real
/// <see cref="ScheduledScanRunner"/> drives the real <see cref="DriverScanService"/>, real
/// update sources, the real <see cref="InstallPipeline"/>, and the real
/// <see cref="JsonDriverCacheStore"/> writing a real file to disk. The next session then reloads
/// that file to prove the round-trip survives serialization.
/// </summary>
public sealed class ScheduledRunAndCacheEndToEndTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    private const string GpuDeviceId = @"PCI\VEN_1002&DEV_747E&SUBSYS_24141458&REV_C8\4&2e5e2d0f&0&0019";
    private const string GpuHardwareId = @"PCI\VEN_1002&DEV_747E&SUBSYS_24141458&REV_C8";

    private static FakeWmiQueryRunner TwoDevices() =>
        new FakeWmiQueryRunner().WithSignedDrivers(
            FakeWmiQueryRunner.SignedDriverRow(
                deviceId: GpuDeviceId,
                deviceName: "AMD Radeon RX 6700 XT",
                driverVersion: "31.0.14051.5006",
                driverDateDmtf: FakeWmiQueryRunner.Dmtf(2023, 5, 2),
                providerName: "Advanced Micro Devices, Inc.",
                deviceClass: "DISPLAY",
                manufacturer: "Advanced Micro Devices, Inc.",
                infName: "oem12.inf"),
            FakeWmiQueryRunner.SignedDriverRow(
                deviceId: @"USB\VID_046D&PID_C52B\5&1a2b3c4d&0&1",
                deviceName: "Logitech USB Receiver",
                driverVersion: "10.0.26100.1",
                driverDateDmtf: FakeWmiQueryRunner.Dmtf(2006, 6, 21),
                providerName: "Microsoft",
                deviceClass: "USB",
                infName: "input.inf"));

    private static UpdateCandidate NewerGpuDriver() => new(
        ForHardwareId: GpuHardwareId,
        Source: UpdateSource.MicrosoftCatalog,
        NewVersion: new Version(31, 0, 24027, 1012),
        NewDate: new DateOnly(2024, 9, 10),
        DownloadUrl: new Uri("https://catalog.update.microsoft.com/download/amd-display.cab"),
        SizeBytes: 550_000_000,
        KbArticle: "KB5001234",
        IsSuperseded: false,
        SourceUpdateId: "catalog-amd-display-31.0.24027.1012",
        SupersededIds: new[] { "older-id" },
        InstallKind: UpdateInstallKind.PnPUtilPackage);

    private ScheduledScanRunner BuildRunner(
        IWmiQueryRunner wmi,
        IDriverCacheStore cacheStore,
        IInstallPipeline pipeline,
        params IUpdateSource[] sources) =>
        new(
            new DriverScanService(wmi, NullLogger<DriverScanService>.Instance),
            sources,
            pipeline,
            cacheStore,
            new StaticOptionsMonitor<UpdaterSettings>(new UpdaterSettings()),
            NullLogger<ScheduledScanRunner>.Instance);

    private InstallPipeline BuildPipeline(IPnPUtilRunner pnputil, IInstalledDriverProbe probe) =>
        new(
            new FakeRestorePointService(),
            new BackupService(
                pnputil,
                new StaticOptionsMonitor<BackupSettings>(new BackupSettings { RootPath = _workspace.Path("Backups") }),
                NullLogger<BackupService>.Instance),
            new FakeWuApiClient(),
            NullLogger<InstallPipeline>.Instance,
            pnputil: pnputil,
            powerShell: new FakeExpandPowerShellInvoker(),
            httpClientFactory: new StubHttpClientFactory("MSCF\0\0\0\0"u8.ToArray()),
            installedDriverProbe: probe);

    private JsonDriverCacheStore NewCacheStore() =>
        new(NullLogger<JsonDriverCacheStore>.Instance, _workspace.Path("driver-cache.json"));

    [Fact]
    public async Task A_scan_only_run_writes_every_scanned_driver_and_its_pending_update_to_the_cache_file()
    {
        var cacheStore = NewCacheStore();
        var pnputil = new FakePnPUtilRunner();
        var runner = BuildRunner(
            TwoDevices(),
            cacheStore,
            BuildPipeline(pnputil, new ScriptedInstalledDriverProbe()),
            new StubUpdateSource(UpdateSource.MicrosoftCatalog, "Microsoft Update Catalog", NewerGpuDriver()));

        await runner.RunAsync(installUpdates: false);

        pnputil.Invocations.Should().BeEmpty("a scan-only run must never install anything");
        File.Exists(cacheStore.CachePath).Should().BeTrue();

        var reloaded = await NewCacheStore().LoadAsync();
        reloaded.Should().NotBeNull();
        reloaded!.Entries.Should().HaveCount(2);

        var gpu = reloaded.Entries.Single(e => e.Driver.DeviceId == GpuDeviceId);
        gpu.Status.Should().Be(DriverStatus.Outdated);
        gpu.AvailableUpdate.Should().NotBeNull();
        gpu.AvailableUpdate!.NewVersion.Should().Be(new Version(31, 0, 24027, 1012));
        gpu.AvailableUpdate.NewDate.Should().Be(new DateOnly(2024, 9, 10));
        gpu.AvailableUpdate.DownloadUrl.Should().Be(NewerGpuDriver().DownloadUrl);
        gpu.AvailableUpdate.KbArticle.Should().Be("KB5001234");
        gpu.AvailableUpdate.SupersededIds.Should().Equal("older-id");
        gpu.Driver.CurrentVersion.Should().Be(new Version(31, 0, 14051, 5006));
        gpu.Driver.CurrentDate.Should().Be(new DateOnly(2023, 5, 2));
        gpu.Driver.HardwareIds.Should().Contain(GpuHardwareId);

        var receiver = reloaded.Entries.Single(e => e.Driver.DeviceClass == "USB");
        receiver.AvailableUpdate.Should().BeNull();
    }

    [Fact]
    public async Task A_scan_and_update_run_installs_the_match_and_clears_it_from_the_cache()
    {
        var cacheStore = NewCacheStore();
        var pnputil = new FakePnPUtilRunner();
        var probe = new ScriptedInstalledDriverProbe()
            .Always(GpuDeviceId, new InstalledDriverState(new Version(31, 0, 24027, 1012), new DateOnly(2024, 9, 10)));
        var runner = BuildRunner(
            TwoDevices(),
            cacheStore,
            BuildPipeline(pnputil, probe),
            new StubUpdateSource(UpdateSource.MicrosoftCatalog, "Microsoft Update Catalog", NewerGpuDriver()));

        await runner.RunAsync(installUpdates: true);

        pnputil.Invocations.Should().Contain(a => a.Contains("/add-driver"));

        var reloaded = await NewCacheStore().LoadAsync();
        var gpu = reloaded!.Entries.Single(e => e.Driver.DeviceId == GpuDeviceId);
        gpu.Status.Should().Be(DriverStatus.UpToDate);
        gpu.AvailableUpdate.Should().BeNull("a successfully installed update must not be re-offered next run");
    }

    [Fact]
    public async Task A_failed_install_keeps_the_device_flagged_so_the_user_can_see_it()
    {
        var cacheStore = NewCacheStore();
        var pnputil = new FakePnPUtilRunner(a =>
            a.Contains("/add-driver")
                ? new ProcessResult(87, string.Empty, "The parameter is incorrect.")
                : new ProcessResult(0, string.Empty, string.Empty));
        var runner = BuildRunner(
            TwoDevices(),
            cacheStore,
            BuildPipeline(pnputil, new ScriptedInstalledDriverProbe()),
            new StubUpdateSource(UpdateSource.MicrosoftCatalog, "Microsoft Update Catalog", NewerGpuDriver()));

        await runner.RunAsync(installUpdates: true);

        var reloaded = await NewCacheStore().LoadAsync();
        var gpu = reloaded!.Entries.Single(e => e.Driver.DeviceId == GpuDeviceId);
        gpu.Status.Should().Be(DriverStatus.Error);
        gpu.AvailableUpdate.Should().NotBeNull("a failed install must stay retryable");
    }

    [Fact]
    public async Task One_shared_installer_covering_many_devices_runs_exactly_once()
    {
        var wmi = new FakeWmiQueryRunner().WithSignedDrivers(
            FakeWmiQueryRunner.SignedDriverRow(
                @"PCI\VEN_1022&DEV_1630\3&11583659&0&00", "AMD PCI Device", "1.0.0.100",
                FakeWmiQueryRunner.Dmtf(2022, 1, 1), "Advanced Micro Devices, Inc.", "SYSTEM", infName: "a.inf"),
            FakeWmiQueryRunner.SignedDriverRow(
                @"PCI\VEN_1022&DEV_1631\3&11583659&0&01", "AMD SMBUS Device", "1.0.0.100",
                FakeWmiQueryRunner.Dmtf(2022, 1, 1), "Advanced Micro Devices, Inc.", "SYSTEM", infName: "b.inf"));

        UpdateCandidate SharedPackage(string hardwareId) => new(
            ForHardwareId: hardwareId,
            Source: UpdateSource.Oem,
            NewVersion: new Version(1, 0, 0, 200),
            NewDate: new DateOnly(2024, 8, 1),
            DownloadUrl: new Uri("https://drivers.amd.com/drivers/amd_chipset_software_7.06.02.123.cab"),
            SizeBytes: 100,
            KbArticle: null,
            IsSuperseded: false,
            SourceUpdateId: "vendor-installer:amd-chipset:7.06.02.123",
            SupersededIds: Array.Empty<string>(),
            InstallKind: UpdateInstallKind.PnPUtilPackage);

        var pnputil = new FakePnPUtilRunner();
        var probe = new ScriptedInstalledDriverProbe()
            .Always(@"PCI\VEN_1022&DEV_1630\3&11583659&0&00", new InstalledDriverState(new Version(1, 0, 0, 200), new DateOnly(2024, 8, 1)))
            .Always(@"PCI\VEN_1022&DEV_1631\3&11583659&0&01", new InstalledDriverState(new Version(1, 0, 0, 200), new DateOnly(2024, 8, 1)));

        var runner = BuildRunner(
            wmi,
            NewCacheStore(),
            BuildPipeline(pnputil, probe),
            new StubUpdateSource(
                UpdateSource.Oem,
                "AMD Chipset",
                SharedPackage(@"PCI\VEN_1022&DEV_1630"),
                SharedPackage(@"PCI\VEN_1022&DEV_1631")));

        await runner.RunAsync(installUpdates: true);

        pnputil.Invocations.Count(a => a.Contains("/add-driver"))
            .Should().Be(1, "18 device rows sharing one chipset package must not run the installer 18 times");

        var reloaded = await NewCacheStore().LoadAsync();
        reloaded!.Entries.Should().OnlyContain(e => e.Status == DriverStatus.UpToDate);
        reloaded.Entries.Should().OnlyContain(e => e.AvailableUpdate == null);
    }

    [Fact]
    public async Task Clearing_the_cache_deletes_the_file_and_reports_how_many_updates_were_dropped()
    {
        var cacheStore = NewCacheStore();
        var runner = BuildRunner(
            TwoDevices(),
            cacheStore,
            BuildPipeline(new FakePnPUtilRunner(), new ScriptedInstalledDriverProbe()),
            new StubUpdateSource(UpdateSource.MicrosoftCatalog, "Microsoft Update Catalog", NewerGpuDriver()));
        await runner.RunAsync(installUpdates: false);

        var raised = 0;
        cacheStore.Cleared += (_, _) => raised++;
        var removed = await cacheStore.ClearAsync();

        removed.Should().Be(1);
        raised.Should().Be(1);
        File.Exists(cacheStore.CachePath).Should().BeFalse();
        (await NewCacheStore().LoadAsync()).Should().BeNull();
    }

    [Fact]
    public async Task A_source_that_throws_does_not_abort_the_whole_scheduled_run()
    {
        var cacheStore = NewCacheStore();
        var runner = BuildRunner(
            TwoDevices(),
            cacheStore,
            BuildPipeline(new FakePnPUtilRunner(), new ScriptedInstalledDriverProbe()),
            new ThrowingUpdateSource(),
            new StubUpdateSource(UpdateSource.MicrosoftCatalog, "Microsoft Update Catalog", NewerGpuDriver()));

        await runner.RunAsync(installUpdates: false);

        var reloaded = await NewCacheStore().LoadAsync();
        reloaded!.Entries.Should().HaveCount(2);
        reloaded.Entries.Single(e => e.Driver.DeviceId == GpuDeviceId).AvailableUpdate.Should().NotBeNull();
    }

    private sealed class ThrowingUpdateSource : IUpdateSource
    {
        public UpdateSource Kind => UpdateSource.Oem;

        public string DisplayName => "Broken vendor source";

        public async IAsyncEnumerable<UpdateCandidate> SearchAsync(
            IReadOnlyCollection<DriverInfo> drivers,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            throw new HttpRequestException("vendor site is down");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }
}
