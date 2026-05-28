using System.Runtime.CompilerServices;
using DriverUpdater.App.Services;
using DriverUpdater.App.Tests.Stubs;
using DriverUpdater.App.ViewModels;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriverUpdater.App.Tests.ViewModels;

public class MainViewModelUpdateSourceTests
{
    [WpfFact]
    public async Task ScanAsync_assigns_update_candidate_to_matching_row()
    {
        var driver = NewDriver("Intel Display", "PCI\\VEN_8086&DEV_4682", new Version(1, 0, 0, 0));
        var candidate = NewCandidate("PCI\\VEN_8086&DEV_4682", new Version(2, 0, 0, 0));

        var vm = NewVm(new[] { driver }, new[] { candidate });

        await vm.ScanCommand.ExecuteAsync(null);

        vm.Drivers.Should().ContainSingle();
        vm.Drivers[0].Status.Should().Be(DriverStatus.Outdated);
        vm.Drivers[0].AvailableUpdate.Should().NotBeNull();
        vm.Drivers[0].AvailableUpdate!.NewVersion.Should().Be(new Version(2, 0, 0, 0));
        vm.UpdatesFoundCount.Should().Be(1);
        vm.ConfirmedUpdatesCount.Should().Be(1);
        vm.VendorChecksCount.Should().Be(0);
    }

    [WpfFact]
    public async Task ScanAsync_ignores_candidate_when_version_is_not_newer()
    {
        var driver = NewDriver("Intel Display", "PCI\\VEN_8086&DEV_4682", new Version(3, 0, 0, 0));
        var candidate = NewCandidate("PCI\\VEN_8086&DEV_4682", new Version(2, 0, 0, 0));

        var vm = NewVm(new[] { driver }, new[] { candidate });

        await vm.ScanCommand.ExecuteAsync(null);

        vm.Drivers[0].Status.Should().Be(DriverStatus.Unknown);
        vm.Drivers[0].AvailableUpdate.Should().BeNull();
        vm.UpdatesFoundCount.Should().Be(0);
        vm.ConfirmedUpdatesCount.Should().Be(0);
        vm.VendorChecksCount.Should().Be(0);
    }

    [WpfFact]
    public async Task ScanAsync_ignores_candidate_with_unmatched_hardware_id()
    {
        var driver = NewDriver("Intel Display", "PCI\\VEN_8086&DEV_4682", new Version(1, 0, 0, 0));
        var candidate = NewCandidate("USB\\VID_046D&PID_0000", new Version(2, 0, 0, 0));

        var vm = NewVm(new[] { driver }, new[] { candidate });

        await vm.ScanCommand.ExecuteAsync(null);

        vm.Drivers[0].AvailableUpdate.Should().BeNull();
    }

    [WpfFact]
    public async Task ScanAsync_continues_when_a_source_throws()
    {
        var driver = NewDriver("Foo", "HW\\FOO", new Version(1, 0, 0, 0));
        var goodCandidate = NewCandidate("HW\\FOO", new Version(2, 0, 0, 0));

        var sources = new IUpdateSource[]
        {
            new ThrowingUpdateSource(),
            new FakeUpdateSource(new[] { goodCandidate })
        };

        var vm = new MainViewModel(
            new FakeScanService(new[] { driver }),
            sources,
            new NullOemDetectionService(),
            new NullInstallPipeline(),
            new NullInstallConfirmation(),
            new NullHistoryWindowOpener(),
            new NullSettingsWindowOpener(),
            NullLogger<MainViewModel>.Instance);

        await vm.ScanCommand.ExecuteAsync(null);

        vm.Drivers[0].AvailableUpdate.Should().NotBeNull();
        vm.UpdatesFoundCount.Should().Be(1);
        vm.ConfirmedUpdatesCount.Should().Be(1);
    }

    [WpfFact]
    public async Task ScanAsync_matches_less_specific_catalog_hardware_id_to_full_device_id()
    {
        var driver = NewDriver("AMD Radeon RX 7700 XT", @"PCI\VEN_1002&DEV_747E&SUBSYS_24141458&REV_FF", new Version(1, 0, 0, 0));
        var candidate = NewCandidate(@"PCI\VEN_1002&DEV_747E", new Version(2, 0, 0, 0));

        var vm = NewVm(new[] { driver }, new[] { candidate });

        await vm.ScanCommand.ExecuteAsync(null);

        vm.Drivers[0].AvailableUpdate.Should().NotBeNull();
        vm.Drivers[0].Status.Should().Be(DriverStatus.Outdated);
    }

    [WpfFact]
    public async Task ScanAsync_does_not_replace_confirmed_update_with_vendor_advisory()
    {
        var driver = NewDriver("Intel Display", "PCI\\VEN_8086&DEV_4682", new Version(1, 0, 0, 0));
        var confirmed = NewCandidate("PCI\\VEN_8086&DEV_4682", new Version(2, 0, 0, 0));
        var advisory = NewCandidate(
            "PCI\\VEN_8086&DEV_4682",
            new Version(2026, 5, 28, 0),
            UpdateInstallKind.VendorPage,
            UpdateConfidence.Advisory);

        var vm = new MainViewModel(
            new FakeScanService(new[] { driver }),
            new IUpdateSource[]
            {
                new FakeUpdateSource(new[] { confirmed }),
                new FakeUpdateSource(new[] { advisory })
            },
            new NullOemDetectionService(),
            new NullInstallPipeline(),
            new NullInstallConfirmation(),
            new NullHistoryWindowOpener(),
            new NullSettingsWindowOpener(),
            NullLogger<MainViewModel>.Instance);

        await vm.ScanCommand.ExecuteAsync(null);

        vm.Drivers[0].AvailableUpdate.Should().BeSameAs(confirmed);
        vm.ConfirmedUpdatesCount.Should().Be(1);
        vm.VendorChecksCount.Should().Be(0);
    }

    [WpfFact]
    public async Task ScanAsync_replaces_vendor_advisory_with_confirmed_update()
    {
        var driver = NewDriver("Intel Display", "PCI\\VEN_8086&DEV_4682", new Version(1, 0, 0, 0));
        var advisory = NewCandidate(
            "PCI\\VEN_8086&DEV_4682",
            new Version(2026, 5, 28, 0),
            UpdateInstallKind.VendorPage,
            UpdateConfidence.Advisory);
        var confirmed = NewCandidate("PCI\\VEN_8086&DEV_4682", new Version(2, 0, 0, 0));

        var vm = new MainViewModel(
            new FakeScanService(new[] { driver }),
            new IUpdateSource[]
            {
                new FakeUpdateSource(new[] { advisory }),
                new FakeUpdateSource(new[] { confirmed })
            },
            new NullOemDetectionService(),
            new NullInstallPipeline(),
            new NullInstallConfirmation(),
            new NullHistoryWindowOpener(),
            new NullSettingsWindowOpener(),
            NullLogger<MainViewModel>.Instance);

        await vm.ScanCommand.ExecuteAsync(null);

        vm.Drivers[0].AvailableUpdate.Should().BeSameAs(confirmed);
        vm.ConfirmedUpdatesCount.Should().Be(1);
        vm.VendorChecksCount.Should().Be(0);
    }

    [WpfFact]
    public async Task UpdateOutdatedAsync_clears_successful_update_and_refreshes_count()
    {
        var driver = NewDriver("Intel Display", "PCI\\VEN_8086&DEV_4682", new Version(1, 0, 0, 0));
        var candidate = NewCandidate("PCI\\VEN_8086&DEV_4682", new Version(2, 0, 0, 0));
        var vm = new MainViewModel(
            new FakeScanService(new[] { driver }),
            new[] { (IUpdateSource)new FakeUpdateSource(new[] { candidate }) },
            new NullOemDetectionService(),
            new SuccessfulInstallPipeline(),
            new ConfirmingInstallConfirmation(),
            new NullHistoryWindowOpener(),
            new NullSettingsWindowOpener(),
            NullLogger<MainViewModel>.Instance);

        await vm.ScanCommand.ExecuteAsync(null);
        await vm.UpdateOutdatedCommand.ExecuteAsync(null);

        vm.Drivers[0].Status.Should().Be(DriverStatus.UpToDate);
        vm.Drivers[0].AvailableUpdate.Should().BeNull();
        vm.UpdatesFoundCount.Should().Be(0);
        vm.ConfirmedUpdatesCount.Should().Be(0);
    }

    [WpfFact]
    public async Task UpdateOutdatedAsync_ignores_vendor_pages_in_automatic_mode()
    {
        var driver = NewDriver("NVIDIA Display", "PCI\\VEN_10DE&DEV_0001", new Version(1, 0, 0, 0));
        var candidate = NewCandidate(
            "PCI\\VEN_10DE&DEV_0001",
            new Version(2026, 5, 28, 0),
            UpdateInstallKind.VendorPage,
            UpdateConfidence.Advisory);
        var opener = new RecordingUpdatePageOpener();
        var vm = new MainViewModel(
            new FakeScanService(new[] { driver }),
            new[] { (IUpdateSource)new FakeUpdateSource(new[] { candidate }) },
            new NullOemDetectionService(),
            new ThrowingInstallPipeline(),
            new ThrowingInstallConfirmation(),
            new NullHistoryWindowOpener(),
            new NullSettingsWindowOpener(),
            NullLogger<MainViewModel>.Instance,
            opener);

        await vm.ScanCommand.ExecuteAsync(null);
        await vm.UpdateOutdatedCommand.ExecuteAsync(null);

        opener.Opened.Should().BeEmpty();
        vm.StatusText.Should().Be("No confirmed updates to install.");
        vm.ConfirmedUpdatesCount.Should().Be(0);
        vm.VendorChecksCount.Should().Be(1);
    }

    [WpfFact]
    public async Task InstallConfirmedAsync_installs_confirmed_updates_without_opening_vendor_pages()
    {
        var confirmedDriver = NewDriver("Intel Display", "PCI\\VEN_8086&DEV_4682", new Version(1, 0, 0, 0));
        var vendorDriver = NewDriver("NVIDIA Display", "PCI\\VEN_10DE&DEV_0001", new Version(1, 0, 0, 0));
        var confirmed = NewCandidate("PCI\\VEN_8086&DEV_4682", new Version(2, 0, 0, 0));
        var advisory = NewCandidate(
            "PCI\\VEN_10DE&DEV_0001",
            new Version(2026, 5, 28, 0),
            UpdateInstallKind.VendorPage,
            UpdateConfidence.Advisory);
        var opener = new RecordingUpdatePageOpener();
        var install = new SuccessfulInstallPipeline();
        var vm = new MainViewModel(
            new FakeScanService(new[] { confirmedDriver, vendorDriver }),
            new[] { (IUpdateSource)new FakeUpdateSource(new[] { confirmed, advisory }) },
            new NullOemDetectionService(),
            install,
            new ConfirmingInstallConfirmation(),
            new NullHistoryWindowOpener(),
            new NullSettingsWindowOpener(),
            NullLogger<MainViewModel>.Instance,
            opener);

        await vm.ScanCommand.ExecuteAsync(null);
        await vm.InstallConfirmedCommand.ExecuteAsync(null);

        opener.Opened.Should().BeEmpty();
        vm.ConfirmedUpdatesCount.Should().Be(0);
        vm.VendorChecksCount.Should().Be(1);
        vm.StatusText.Should().Contain("confirmed drivers");
    }

    [WpfFact]
    public async Task OpenVendorChecksCommand_opens_only_vendor_pages()
    {
        var confirmedDriver = NewDriver("Intel Display", "PCI\\VEN_8086&DEV_4682", new Version(1, 0, 0, 0));
        var vendorDriver = NewDriver("NVIDIA Display", "PCI\\VEN_10DE&DEV_0001", new Version(1, 0, 0, 0));
        var confirmed = NewCandidate("PCI\\VEN_8086&DEV_4682", new Version(2, 0, 0, 0));
        var advisory = NewCandidate(
            "PCI\\VEN_10DE&DEV_0001",
            new Version(2026, 5, 28, 0),
            UpdateInstallKind.VendorPage,
            UpdateConfidence.Advisory);
        var opener = new RecordingUpdatePageOpener();
        var vm = new MainViewModel(
            new FakeScanService(new[] { confirmedDriver, vendorDriver }),
            new[] { (IUpdateSource)new FakeUpdateSource(new[] { confirmed, advisory }) },
            new NullOemDetectionService(),
            new ThrowingInstallPipeline(),
            new ThrowingInstallConfirmation(),
            new NullHistoryWindowOpener(),
            new NullSettingsWindowOpener(),
            NullLogger<MainViewModel>.Instance,
            opener);

        await vm.ScanCommand.ExecuteAsync(null);
        vm.OpenVendorChecksCommand.Execute(null);

        opener.Opened.Should().ContainSingle().Which.Should().Be(advisory.DownloadUrl);
        vm.ConfirmedUpdatesCount.Should().Be(1);
        vm.VendorChecksCount.Should().Be(1);
    }

    [WpfFact]
    public async Task Update_commands_are_enabled_only_when_matching_work_exists()
    {
        var confirmedDriver = NewDriver("Intel Display", "PCI\\VEN_8086&DEV_4682", new Version(1, 0, 0, 0));
        var vendorDriver = NewDriver("NVIDIA Display", "PCI\\VEN_10DE&DEV_0001", new Version(1, 0, 0, 0));
        var confirmed = NewCandidate("PCI\\VEN_8086&DEV_4682", new Version(2, 0, 0, 0));
        var advisory = NewCandidate(
            "PCI\\VEN_10DE&DEV_0001",
            new Version(2026, 5, 28, 0),
            UpdateInstallKind.VendorPage,
            UpdateConfidence.Advisory);
        var vm = new MainViewModel(
            new FakeScanService(new[] { confirmedDriver, vendorDriver }),
            new[] { (IUpdateSource)new FakeUpdateSource(new[] { confirmed, advisory }) },
            new NullOemDetectionService(),
            new NullInstallPipeline(),
            new NullInstallConfirmation(),
            new NullHistoryWindowOpener(),
            new NullSettingsWindowOpener(),
            NullLogger<MainViewModel>.Instance,
            new RecordingUpdatePageOpener());

        vm.UpdateOutdatedCommand.CanExecute(null).Should().BeFalse();
        vm.InstallConfirmedCommand.CanExecute(null).Should().BeFalse();
        vm.OpenVendorChecksCommand.CanExecute(null).Should().BeFalse();

        await vm.ScanCommand.ExecuteAsync(null);

        vm.UpdateOutdatedCommand.CanExecute(null).Should().BeTrue();
        vm.InstallConfirmedCommand.CanExecute(null).Should().BeTrue();
        vm.OpenVendorChecksCommand.CanExecute(null).Should().BeTrue();
    }

    [WpfFact]
    public async Task OpenVendorChecksCommand_is_disabled_without_page_opener()
    {
        var driver = NewDriver("NVIDIA Display", "PCI\\VEN_10DE&DEV_0001", new Version(1, 0, 0, 0));
        var advisory = NewCandidate(
            "PCI\\VEN_10DE&DEV_0001",
            new Version(2026, 5, 28, 0),
            UpdateInstallKind.VendorPage,
            UpdateConfidence.Advisory);
        var vm = NewVm(new[] { driver }, new[] { advisory });

        await vm.ScanCommand.ExecuteAsync(null);

        vm.VendorChecksCount.Should().Be(1);
        vm.OpenVendorChecksCommand.CanExecute(null).Should().BeFalse();
    }

    [WpfFact]
    public async Task Update_filter_narrows_confirmed_vendor_and_no_update_rows()
    {
        var confirmedDriver = NewDriver("Intel Display", "PCI\\VEN_8086&DEV_4682", new Version(1, 0, 0, 0));
        var vendorDriver = NewDriver("NVIDIA Display", "PCI\\VEN_10DE&DEV_0001", new Version(1, 0, 0, 0));
        var currentDriver = NewDriver("Current Display", "PCI\\VEN_1234&DEV_0001", new Version(9, 0, 0, 0));
        var confirmed = NewCandidate("PCI\\VEN_8086&DEV_4682", new Version(2, 0, 0, 0));
        var advisory = NewCandidate(
            "PCI\\VEN_10DE&DEV_0001",
            new Version(2026, 5, 28, 0),
            UpdateInstallKind.VendorPage,
            UpdateConfidence.Advisory);

        var vm = NewVm(new[] { confirmedDriver, vendorDriver, currentDriver }, new[] { confirmed, advisory });
        await vm.ScanCommand.ExecuteAsync(null);

        vm.UpdateFilter = DriverUpdateFilter.ConfirmedUpdates;
        vm.DriversView.Cast<DriverRowViewModel>().Should().ContainSingle(r => r.DeviceName == "Intel Display");

        vm.UpdateFilter = DriverUpdateFilter.VendorChecks;
        vm.DriversView.Cast<DriverRowViewModel>().Should().ContainSingle(r => r.DeviceName == "NVIDIA Display");

        vm.UpdateFilter = DriverUpdateFilter.NoUpdate;
        vm.DriversView.Cast<DriverRowViewModel>().Should().ContainSingle(r => r.DeviceName == "Current Display");
    }

    private static DriverInfo NewDriver(string name, string hardwareId, Version version) => new(
        DeviceId: $"ID\\{name}",
        HardwareId: hardwareId,
        DeviceName: name,
        Category: DriverCategory.Display,
        Provider: "Intel",
        Manufacturer: "Intel Corporation",
        CurrentVersion: version,
        CurrentDate: new DateOnly(2024, 1, 1),
        InfName: "oem.inf",
        InfPath: null,
        IsSigned: true,
        DeviceClass: "Display");

    private static UpdateCandidate NewCandidate(
        string hardwareId,
        Version newVersion,
        UpdateInstallKind installKind = UpdateInstallKind.WindowsUpdate,
        UpdateConfidence confidence = UpdateConfidence.Confirmed) => new(
        ForHardwareId: hardwareId,
        Source: UpdateSource.WindowsUpdate,
        NewVersion: newVersion,
        NewDate: new DateOnly(2026, 1, 1),
        DownloadUrl: new Uri("https://example.com/x.cab"),
        SizeBytes: 1024,
        KbArticle: null,
        IsSuperseded: false,
        SourceUpdateId: Guid.NewGuid().ToString(),
        SupersededIds: Array.Empty<string>(),
        InstallKind: installKind,
        Confidence: confidence);

    private static MainViewModel NewVm(IEnumerable<DriverInfo> drivers, IEnumerable<UpdateCandidate> candidates) =>
        new(new FakeScanService(drivers),
            new[] { (IUpdateSource)new FakeUpdateSource(candidates) },
            new NullOemDetectionService(),
            new NullInstallPipeline(),
            new NullInstallConfirmation(),
            new NullHistoryWindowOpener(),
            new NullSettingsWindowOpener(),
            NullLogger<MainViewModel>.Instance);

    private sealed class FakeScanService : IDriverScanService
    {
        private readonly IEnumerable<DriverInfo> _drivers;

        public FakeScanService(IEnumerable<DriverInfo> drivers)
        {
            _drivers = drivers;
        }

        public async IAsyncEnumerable<DriverInfo> ScanAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var driver in _drivers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return driver;
            }
        }
    }

    private sealed class FakeUpdateSource : IUpdateSource
    {
        private readonly IEnumerable<UpdateCandidate> _candidates;

        public FakeUpdateSource(IEnumerable<UpdateCandidate> candidates)
        {
            _candidates = candidates;
        }

        public UpdateSource Kind => UpdateSource.WindowsUpdate;
        public string DisplayName => "Fake";

        public async IAsyncEnumerable<UpdateCandidate> SearchAsync(
            IReadOnlyCollection<DriverInfo> drivers,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var candidate in _candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return candidate;
            }
        }
    }

    private sealed class ThrowingUpdateSource : IUpdateSource
    {
        public UpdateSource Kind => UpdateSource.MicrosoftCatalog;
        public string DisplayName => "Throwing source";

#pragma warning disable CS1998
        public async IAsyncEnumerable<UpdateCandidate> SearchAsync(
            IReadOnlyCollection<DriverInfo> drivers,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("source unavailable");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
#pragma warning restore CS1998
    }

    private sealed class ConfirmingInstallConfirmation : IInstallConfirmation
    {
        public InstallOptions? Confirm(UpdateOperation operation, bool dryRun) =>
            new(CreateRestorePoint: false, BackupCurrentDriver: false, DryRun: dryRun);
    }

    private sealed class SuccessfulInstallPipeline : IInstallPipeline
    {
        public Task<UpdateOperation> ExecuteAsync(
            UpdateOperation operation,
            InstallOptions options,
            IProgress<UpdateOperation>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var finished = operation with
            {
                Status = UpdateStatus.Succeeded,
                CompletedAt = DateTimeOffset.UtcNow
            };
            progress?.Report(finished);
            return Task.FromResult(finished);
        }
    }

    private sealed class RecordingUpdatePageOpener : IUpdatePageOpener
    {
        public List<Uri> Opened { get; } = new();

        public void Open(UpdateCandidate candidate) => Opened.Add(candidate.DownloadUrl);
    }

    private sealed class ThrowingInstallPipeline : IInstallPipeline
    {
        public Task<UpdateOperation> ExecuteAsync(
            UpdateOperation operation,
            InstallOptions options,
            IProgress<UpdateOperation>? progress = null,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("install should not be called");
    }

    private sealed class ThrowingInstallConfirmation : IInstallConfirmation
    {
        public InstallOptions? Confirm(UpdateOperation operation, bool dryRun) =>
            throw new InvalidOperationException("confirmation should not be called");
    }
}
