using System.IO;
using DriverUpdater.App.ViewModels;
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
/// Drives the interactive app the way a user does - Scan, then Update - through the real
/// <see cref="MainViewModel"/> wired to real production services: real driver scan, real install
/// pipeline, real JSON cache store, and the real ineffective-update ledger, all writing to real
/// files. Only the OS boundaries and the modal dialogs are substituted.
/// </summary>
public sealed class InteractiveAppEndToEndTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    private const string AudioDeviceId = @"HDAUDIO\FUNC_01&VEN_10EC&DEV_0897\4&1a2b3c4d&0&0001";
    private const string AudioHardwareId = @"HDAUDIO\FUNC_01&VEN_10EC&DEV_0897";

    private static FakeWmiQueryRunner OneOutdatedAudioCard() =>
        new FakeWmiQueryRunner().WithSignedDrivers(
            FakeWmiQueryRunner.SignedDriverRow(
                deviceId: AudioDeviceId,
                deviceName: "Realtek High Definition Audio",
                driverVersion: "6.0.9235.1",
                driverDateDmtf: FakeWmiQueryRunner.Dmtf(2022, 2, 10),
                providerName: "Realtek Semiconductor Corp.",
                deviceClass: "MEDIA",
                manufacturer: "Realtek",
                infName: "oem7.inf",
                hardwareIds: new[] { AudioHardwareId }),
            FakeWmiQueryRunner.SignedDriverRow(
                deviceId: @"ROOT\SYSTEM\0001",
                deviceName: "Microsoft Print to PDF",
                driverVersion: "10.0.26100.1",
                driverDateDmtf: FakeWmiQueryRunner.Dmtf(2006, 6, 21),
                providerName: "Microsoft",
                deviceClass: "PRINTER"));

    private static UpdateCandidate NewerAudioDriver(UpdateInstallKind kind = UpdateInstallKind.PnPUtilPackage) => new(
        ForHardwareId: AudioHardwareId,
        Source: UpdateSource.MicrosoftCatalog,
        NewVersion: new Version(6, 0, 9536, 1),
        NewDate: new DateOnly(2024, 4, 18),
        DownloadUrl: new Uri("https://catalog.update.microsoft.com/download/realtek-audio.cab"),
        SizeBytes: 40_000_000,
        KbArticle: null,
        IsSuperseded: false,
        SourceUpdateId: "catalog-realtek-6.0.9536.1",
        SupersededIds: Array.Empty<string>(),
        InstallKind: kind);

    private sealed record App(
        MainViewModel ViewModel,
        JsonDriverCacheStore CacheStore,
        JsonIneffectiveUpdateStore IneffectiveStore,
        FakePnPUtilRunner PnPUtil,
        ScriptedInstallConfirmation Confirmation,
        RecordingUpdatePageOpener PageOpener,
        ScriptedRebootPrompt RebootPrompt);

    private App BuildApp(
        IWmiQueryRunner wmi,
        IInstalledDriverProbe probe,
        InstallOptions? confirmationAnswer,
        params IUpdateSource[] sources) =>
        BuildApp(wmi, probe, confirmationAnswer, pnputilResponder: null, acceptRestart: false, sources);

    private App BuildApp(
        IWmiQueryRunner wmi,
        IInstalledDriverProbe probe,
        InstallOptions? confirmationAnswer,
        Func<string, ProcessResult>? pnputilResponder,
        bool acceptRestart,
        params IUpdateSource[] sources)
    {
        var pnputil = new FakePnPUtilRunner(pnputilResponder);
        var cacheStore = new JsonDriverCacheStore(
            NullLogger<JsonDriverCacheStore>.Instance, _workspace.Path("driver-cache.json"));
        var ineffectiveStore = new JsonIneffectiveUpdateStore(
            NullLogger<JsonIneffectiveUpdateStore>.Instance, _workspace.Path("ineffective-updates.json"));
        var pipeline = new InstallPipeline(
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

        var confirmation = new ScriptedInstallConfirmation(confirmationAnswer);
        var pageOpener = new RecordingUpdatePageOpener();
        var rebootPrompt = new ScriptedRebootPrompt(acceptRestart);
        var windows = new RecordingWindowOpener();

        var viewModel = new MainViewModel(
            new DriverScanService(wmi, NullLogger<DriverScanService>.Instance),
            sources,
            new NoOemDetectionService(),
            pipeline,
            confirmation,
            windows,
            windows,
            windows,
            NullLogger<MainViewModel>.Instance,
            updatePageOpener: pageOpener,
            driverCacheStore: cacheStore,
            updaterSettings: new StaticOptionsMonitor<UpdaterSettings>(new UpdaterSettings()),
            rebootPrompt: rebootPrompt,
            ineffectiveUpdateStore: ineffectiveStore);

        return new App(viewModel, cacheStore, ineffectiveStore, pnputil, confirmation, pageOpener, rebootPrompt);
    }

    [WpfFact]
    public async Task Scanning_lists_every_driver_and_flags_the_one_with_an_update()
    {
        var app = BuildApp(
            OneOutdatedAudioCard(),
            new ScriptedInstalledDriverProbe(),
            confirmationAnswer: null,
            new StubUpdateSource(UpdateSource.MicrosoftCatalog, "Microsoft Update Catalog", NewerAudioDriver()));

        await app.ViewModel.ScanCommand.ExecuteAsync(null);

        app.ViewModel.Drivers.Should().HaveCount(2);
        app.ViewModel.ScannedCount.Should().Be(2);
        app.ViewModel.ConfirmedUpdatesCount.Should().Be(1);
        app.ViewModel.VendorChecksCount.Should().Be(0);

        var audioRow = app.ViewModel.Drivers.Single(r => r.Driver.DeviceId == AudioDeviceId);
        audioRow.HasAvailableUpdate.Should().BeTrue();
        audioRow.CanUpdate.Should().BeTrue();
        audioRow.Status.Should().Be(DriverStatus.Outdated);
        audioRow.AvailableVersionText.Should().Be("6.0.9536.1");

        var printerRow = app.ViewModel.Drivers.Single(r => r.Driver.DeviceClass == "PRINTER");
        printerRow.HasAvailableUpdate.Should().BeFalse();
        printerRow.Status.Should().Be(DriverStatus.NotFound);

        File.Exists(app.CacheStore.CachePath).Should().BeTrue("a finished scan must persist its result");
    }

    [WpfFact]
    public async Task Update_all_installs_the_confirmed_update_and_marks_the_row_up_to_date()
    {
        var probe = new ScriptedInstalledDriverProbe()
            .Always(AudioDeviceId, new InstalledDriverState(new Version(6, 0, 9536, 1), new DateOnly(2024, 4, 18)));
        var app = BuildApp(
            OneOutdatedAudioCard(),
            probe,
            new InstallOptions(CreateRestorePoint: true, BackupCurrentDriver: true, DryRun: false),
            new StubUpdateSource(UpdateSource.MicrosoftCatalog, "Microsoft Update Catalog", NewerAudioDriver()));

        await app.ViewModel.ScanCommand.ExecuteAsync(null);
        await app.ViewModel.UpdateAllCommand.ExecuteAsync(null);

        app.Confirmation.Prompts.Should().ContainSingle("the user is asked once per run, not once per driver");
        app.PnPUtil.Invocations.Should().Contain(a => a.Contains("/add-driver"));

        var audioRow = app.ViewModel.Drivers.Single(r => r.Driver.DeviceId == AudioDeviceId);
        audioRow.Status.Should().Be(DriverStatus.UpToDate);
        audioRow.AvailableUpdate.Should().BeNull();
        app.ViewModel.ConfirmedUpdatesCount.Should().Be(0);
    }

    [WpfFact]
    public async Task Declining_the_confirmation_dialog_installs_nothing()
    {
        var app = BuildApp(
            OneOutdatedAudioCard(),
            new ScriptedInstalledDriverProbe(),
            confirmationAnswer: null,
            new StubUpdateSource(UpdateSource.MicrosoftCatalog, "Microsoft Update Catalog", NewerAudioDriver()));

        await app.ViewModel.ScanCommand.ExecuteAsync(null);
        await app.ViewModel.UpdateAllCommand.ExecuteAsync(null);

        app.PnPUtil.Invocations.Should().NotContain(a => a.Contains("/add-driver"));
        app.ViewModel.StatusText.Should().Be("Update cancelled.");
        app.ViewModel.Drivers.Single(r => r.Driver.DeviceId == AudioDeviceId)
            .AvailableUpdate.Should().NotBeNull();
    }

    [WpfFact]
    public async Task A_proven_no_op_update_is_recorded_and_no_longer_offered_on_the_next_scan()
    {
        // Windows keeps the installed driver, so the install is a proven no-op.
        var probe = new ScriptedInstalledDriverProbe()
            .Always(AudioDeviceId, new InstalledDriverState(new Version(6, 0, 9235, 1), new DateOnly(2022, 2, 10)));
        var app = BuildApp(
            OneOutdatedAudioCard(),
            probe,
            new InstallOptions(CreateRestorePoint: false, BackupCurrentDriver: false, DryRun: false),
            new StubUpdateSource(UpdateSource.MicrosoftCatalog, "Microsoft Update Catalog", NewerAudioDriver()));

        await app.ViewModel.ScanCommand.ExecuteAsync(null);
        await app.ViewModel.UpdateAllCommand.ExecuteAsync(null);

        var records = await app.IneffectiveStore.LoadAsync();
        records.Should().ContainSingle();
        records[0].DeviceId.Should().Be(AudioDeviceId);
        records[0].TargetVersion.Should().Be("6.0.9536.1");
        records[0].InstalledVersionAtAttempt.Should().Be("6.0.9235.1");

        // Second scan in the same session: the catalog still offers the same package.
        await app.ViewModel.ScanCommand.ExecuteAsync(null);

        app.ViewModel.Drivers.Single(r => r.Driver.DeviceId == AudioDeviceId)
            .AvailableUpdate.Should().BeNull("re-offering a proven no-op puts the app in an install loop");
        app.ViewModel.ConfirmedUpdatesCount.Should().Be(0);
    }

    [WpfFact]
    public async Task A_vendor_page_row_that_cannot_be_installed_falls_back_to_opening_the_page()
    {
        var app = BuildApp(
            OneOutdatedAudioCard(),
            new ScriptedInstalledDriverProbe(),
            new InstallOptions(CreateRestorePoint: false, BackupCurrentDriver: false, DryRun: false),
            new StubUpdateSource(
                UpdateSource.Oem,
                "Vendor support page",
                NewerAudioDriver(UpdateInstallKind.VendorPage) with
                {
                    DownloadUrl = new Uri("https://www.realtek.com/downloads/audio"),
                    Confidence = UpdateConfidence.Advisory
                }));

        await app.ViewModel.ScanCommand.ExecuteAsync(null);
        app.ViewModel.VendorChecksCount.Should().Be(1);

        await app.ViewModel.UpdateAllCommand.ExecuteAsync(null);

        app.PageOpener.Opened.Should().ContainSingle()
            .Which.Should().Be(new Uri("https://www.realtek.com/downloads/audio"));
        app.ViewModel.Drivers.Single(r => r.Driver.DeviceId == AudioDeviceId)
            .Status.Should().Be(DriverStatus.ManualActionRequired);
    }

    [WpfFact]
    public async Task A_reboot_required_install_asks_the_user_once_and_restarts_when_accepted()
    {
        var probe = new ScriptedInstalledDriverProbe();
        var app = BuildApp(
            OneOutdatedAudioCard(),
            probe,
            new InstallOptions(CreateRestorePoint: false, BackupCurrentDriver: false, DryRun: false),
            // ERROR_SUCCESS_REBOOT_REQUIRED: the package is staged, the driver binds after restart.
            pnputilResponder: a => a.Contains("/add-driver")
                ? new ProcessResult(3010, "System reboot is needed to complete install operations!", string.Empty)
                : new ProcessResult(0, string.Empty, string.Empty),
            acceptRestart: true,
            new StubUpdateSource(UpdateSource.MicrosoftCatalog, "Microsoft Update Catalog", NewerAudioDriver()));

        await app.ViewModel.ScanCommand.ExecuteAsync(null);
        await app.ViewModel.UpdateAllCommand.ExecuteAsync(null);

        app.RebootPrompt.PromptCount.Should().Be(1);
        app.RebootPrompt.LastPromptedDriverCount.Should().Be(1);
        app.RebootPrompt.RestartCount.Should().Be(1);
        probe.ProbedDeviceIds.Should().BeEmpty("verification must wait until after the restart");

        var records = await app.IneffectiveStore.LoadAsync();
        records.Should().BeEmpty("a reboot-pending install is not a proven no-op and must stay offered");
    }

    [WpfFact]
    public async Task The_second_session_shows_the_previous_scan_from_cache_and_marks_it_stale()
    {
        var app = BuildApp(
            OneOutdatedAudioCard(),
            new ScriptedInstalledDriverProbe(),
            confirmationAnswer: null,
            new StubUpdateSource(UpdateSource.MicrosoftCatalog, "Microsoft Update Catalog", NewerAudioDriver()));
        await app.ViewModel.ScanCommand.ExecuteAsync(null);

        var secondSession = BuildApp(
            OneOutdatedAudioCard(),
            new ScriptedInstalledDriverProbe(),
            confirmationAnswer: null,
            new StubUpdateSource(UpdateSource.MicrosoftCatalog, "Microsoft Update Catalog", NewerAudioDriver()));
        await secondSession.ViewModel.InitializeAsync();

        secondSession.ViewModel.IsShowingCachedDrivers.Should().BeTrue();
        secondSession.ViewModel.Drivers.Should().HaveCount(2);

        var cachedAudioRow = secondSession.ViewModel.Drivers.Single(r => r.Driver.DeviceId == AudioDeviceId);
        cachedAudioRow.AvailableUpdate.Should().NotBeNull();
        cachedAudioRow.IsUpdateFromCache.Should().BeTrue();
        cachedAudioRow.HasAvailableUpdate.Should().BeFalse("a cached result must be re-verified by a fresh scan");
        cachedAudioRow.CanUpdate.Should().BeFalse();
        cachedAudioRow.StatusText.Should().Be("Cached result, re-scan required");
    }

    [WpfFact]
    public async Task Filters_narrow_the_grid_without_losing_rows()
    {
        var app = BuildApp(
            OneOutdatedAudioCard(),
            new ScriptedInstalledDriverProbe(),
            confirmationAnswer: null,
            new StubUpdateSource(UpdateSource.MicrosoftCatalog, "Microsoft Update Catalog", NewerAudioDriver()));
        await app.ViewModel.ScanCommand.ExecuteAsync(null);

        app.ViewModel.UpdateFilter = DriverUpdateFilter.UpdatesAvailable;
        app.ViewModel.DriversView.Cast<DriverRowViewModel>().Should().ContainSingle()
            .Which.Driver.DeviceId.Should().Be(AudioDeviceId);

        app.ViewModel.UpdateFilter = DriverUpdateFilter.NoUpdateAvailable;
        app.ViewModel.DriversView.Cast<DriverRowViewModel>().Should().ContainSingle()
            .Which.Driver.DeviceClass.Should().Be("PRINTER");

        app.ViewModel.UpdateFilter = DriverUpdateFilter.AllDrivers;
        app.ViewModel.SearchText = "realtek";
        app.ViewModel.DriversView.Cast<DriverRowViewModel>().Should().ContainSingle()
            .Which.Driver.DeviceId.Should().Be(AudioDeviceId);

        app.ViewModel.SearchText = string.Empty;
        app.ViewModel.CategoryFilter = DriverCategory.Audio;
        app.ViewModel.DriversView.Cast<DriverRowViewModel>().Should().ContainSingle()
            .Which.Driver.DeviceId.Should().Be(AudioDeviceId);
    }

    [WpfFact]
    public async Task A_downgrade_offered_by_a_source_is_never_shown_to_the_user()
    {
        var downgrade = NewerAudioDriver() with
        {
            NewVersion = new Version(2018, 5, 31, 0),
            NewDate = new DateOnly(2018, 5, 31),
            SourceUpdateId = "catalog-old-realtek"
        };
        var app = BuildApp(
            OneOutdatedAudioCard(),
            new ScriptedInstalledDriverProbe(),
            confirmationAnswer: null,
            new StubUpdateSource(UpdateSource.MicrosoftCatalog, "Microsoft Update Catalog", downgrade));

        await app.ViewModel.ScanCommand.ExecuteAsync(null);

        app.ViewModel.Drivers.Single(r => r.Driver.DeviceId == AudioDeviceId)
            .AvailableUpdate.Should().BeNull();
        app.ViewModel.ConfirmedUpdatesCount.Should().Be(0);
    }
}
