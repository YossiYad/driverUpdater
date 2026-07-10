using System.Runtime.CompilerServices;
using DriverUpdater.App.Tests.Stubs;
using DriverUpdater.App.ViewModels;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriverUpdater.App.Tests.ViewModels;

public class MainViewModelIneffectiveTests
{
    [WpfFact]
    public async Task ScanAsync_suppresses_catalog_candidate_at_device_level_even_with_a_different_build()
    {
        // The catalog re-versions the same generic driver (30.100.2534.35 recorded, .18 offered).
        // Device-level suppression must skip it while the installed version is unchanged.
        var driver = NewDriver("Computer Device", "SWD\\COMPUTER\\ASUS", new Version(10, 0, 26100, 1));
        var candidate = NewCatalogCandidate(driver.HardwareId, new Version(30, 100, 2534, 18));
        var store = new FakeIneffectiveStore(new IneffectiveUpdateRecord(
            driver.DeviceId, "30.100.2534.35", "10.0.26100.1", 1, DateTimeOffset.UtcNow));

        var vm = NewVm(new[] { driver }, new[] { candidate }, store);
        await vm.ScanCommand.ExecuteAsync(null);

        vm.Drivers[0].AvailableUpdate.Should().BeNull("a catalog driver already failed to replace 10.0.26100.1");
        vm.UpdatesFoundCount.Should().Be(0);
    }

    [WpfFact]
    public async Task ScanAsync_offers_catalog_candidate_again_after_the_installed_version_changed()
    {
        // The device now reports a different installed version than when the no-op was recorded,
        // so the ledger no longer applies and the candidate is offered normally.
        var driver = NewDriver("Computer Device", "SWD\\COMPUTER\\ASUS", new Version(10, 0, 26100, 9));
        var candidate = NewCatalogCandidate(driver.HardwareId, new Version(30, 100, 2534, 18));
        var store = new FakeIneffectiveStore(new IneffectiveUpdateRecord(
            driver.DeviceId, "30.100.2534.35", "10.0.26100.1", 1, DateTimeOffset.UtcNow));

        var vm = NewVm(new[] { driver }, new[] { candidate }, store);
        await vm.ScanCommand.ExecuteAsync(null);

        vm.Drivers[0].AvailableUpdate.Should().NotBeNull("the installed version changed since the recorded no-op");
    }

    private static MainViewModel NewVm(
        IEnumerable<DriverInfo> drivers,
        IEnumerable<UpdateCandidate> candidates,
        IIneffectiveUpdateStore store) =>
        new(new FakeScanService(drivers),
            new[] { (IUpdateSource)new FakeUpdateSource(candidates) },
            new NullOemDetectionService(),
            new NullInstallPipeline(),
            new NullInstallConfirmation(),
            new NullHistoryWindowOpener(),
            new NullSettingsWindowOpener(),
            new NullLogsWindowOpener(),
            NullLogger<MainViewModel>.Instance,
            ineffectiveUpdateStore: store);

    private static DriverInfo NewDriver(string name, string hardwareId, Version version) => new(
        DeviceId: $"{hardwareId}\\INSTANCE",
        HardwareId: hardwareId,
        DeviceName: name,
        Category: DriverCategory.System,
        Provider: "Microsoft",
        Manufacturer: "Microsoft",
        CurrentVersion: version,
        CurrentDate: new DateOnly(2006, 6, 21),
        InfName: "oem.inf",
        InfPath: null,
        IsSigned: true,
        DeviceClass: "System");

    private static UpdateCandidate NewCatalogCandidate(string hardwareId, Version newVersion) => new(
        ForHardwareId: hardwareId,
        Source: UpdateSource.MicrosoftCatalog,
        NewVersion: newVersion,
        NewDate: new DateOnly(2026, 3, 1),
        DownloadUrl: new Uri("https://catalog.example.com/x.cab"),
        SizeBytes: 1024,
        KbArticle: null,
        IsSuperseded: false,
        SourceUpdateId: "catalog-" + newVersion,
        SupersededIds: Array.Empty<string>(),
        InstallKind: UpdateInstallKind.PnPUtilPackage,
        Confidence: UpdateConfidence.Confirmed);

    private sealed class FakeIneffectiveStore : IIneffectiveUpdateStore
    {
        private readonly List<IneffectiveUpdateRecord> _records;

        public FakeIneffectiveStore(params IneffectiveUpdateRecord[] records) => _records = records.ToList();

        public Task<IReadOnlyList<IneffectiveUpdateRecord>> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<IneffectiveUpdateRecord>>(_records);

        public Task RecordAsync(string deviceId, string targetVersion, string? installedVersion, CancellationToken cancellationToken = default)
        {
            _records.Add(new IneffectiveUpdateRecord(deviceId, targetVersion, installedVersion, 1, DateTimeOffset.UtcNow));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeScanService : IDriverScanService
    {
        private readonly IEnumerable<DriverInfo> _drivers;
        public FakeScanService(IEnumerable<DriverInfo> drivers) => _drivers = drivers;

        public async IAsyncEnumerable<DriverInfo> ScanAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var driver in _drivers)
            {
                await Task.Yield();
                yield return driver;
            }
        }
    }

    private sealed class FakeUpdateSource : IUpdateSource
    {
        private readonly IEnumerable<UpdateCandidate> _candidates;
        public FakeUpdateSource(IEnumerable<UpdateCandidate> candidates) => _candidates = candidates;

        public UpdateSource Kind => UpdateSource.MicrosoftCatalog;
        public string DisplayName => "Fake Catalog";

        public async IAsyncEnumerable<UpdateCandidate> SearchAsync(
            IReadOnlyCollection<DriverInfo> drivers,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var candidate in _candidates)
            {
                await Task.Yield();
                yield return candidate;
            }
        }
    }
}
