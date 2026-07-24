using System.Runtime.CompilerServices;
using DriverUpdater.App.Services;
using DriverUpdater.App.Tests.Stubs;
using DriverUpdater.App.ViewModels;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using DriverUpdater.Core.Options;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

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

        vm.Drivers[0].Status.Should().Be(DriverStatus.NotFound);
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
            new NullLogsWindowOpener(),
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
    public async Task ScanAsync_matches_update_candidate_to_alternate_hardware_id()
    {
        var driver = NewDriver("Intel Chipset", @"PCI\VEN_8086&DEV_1234&REV_01", new Version(1, 0, 0, 0)) with
        {
            HardwareIds =
            [
                @"PCI\VEN_8086&DEV_1234&REV_01",
                @"PCI\VEN_8086&DEV_1234&SUBSYS_00000000"
            ]
        };
        var candidate = NewCandidate(@"PCI\VEN_8086&DEV_1234&SUBSYS_00000000", new Version(2, 0, 0, 0));

        var vm = NewVm(new[] { driver }, new[] { candidate });

        await vm.ScanCommand.ExecuteAsync(null);

        vm.Drivers[0].AvailableUpdate.Should().NotBeNull();
        vm.Drivers[0].Status.Should().Be(DriverStatus.Outdated);
    }

    [Theory]
    // Same string and clean prefix-with-separator cases match.
    [InlineData(@"PCI\VEN_1002&DEV_747E", @"PCI\VEN_1002&DEV_747E", true)]
    [InlineData(@"PCI\VEN_1002&DEV_747E", @"PCI\VEN_1002&DEV_747E&SUBSYS_24141458", true)]
    [InlineData(@"PCI\VEN_1002&DEV_747E&SUBSYS_24141458", @"PCI\VEN_1002&DEV_747E", true)]
    [InlineData(@"USB\VID_046D", @"USB\VID_046D&PID_0001", true)]
    [InlineData(@"ROOT\X", @"ROOT\X\0001", true)]
    // Coincidental prefixes without a separator boundary do not match.
    [InlineData(@"ROOT\X", @"ROOT\XYZ", false)]
    [InlineData(@"PCI\VEN_10", @"PCI\VEN_1002", false)]
    [InlineData(@"PCI\VEN_1022", @"ROOT\LGHUB_VBUS", false)]
    [InlineData(@"HID\VID_046D", @"PCI\VEN_046D", false)]
    public void IsBoundaryPrefix_only_matches_when_remainder_starts_at_separator(string a, string b, bool expected)
    {
        DriverUpdater.App.ViewModels.MainViewModel.IsBoundaryPrefix(a, b).Should().Be(expected);
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
            new NullLogsWindowOpener(),
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
            new NullLogsWindowOpener(),
            NullLogger<MainViewModel>.Instance);

        await vm.ScanCommand.ExecuteAsync(null);

        vm.Drivers[0].AvailableUpdate.Should().BeSameAs(confirmed);
        vm.ConfirmedUpdatesCount.Should().Be(1);
        vm.VendorChecksCount.Should().Be(0);
    }

    [WpfFact]
    public async Task UpdateAllAsync_clears_successful_update_and_refreshes_count()
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
            new NullLogsWindowOpener(),
            NullLogger<MainViewModel>.Instance);

        await vm.ScanCommand.ExecuteAsync(null);
        await vm.UpdateAllCommand.ExecuteAsync(null);

        vm.Drivers[0].Status.Should().Be(DriverStatus.UpToDate);
        vm.Drivers[0].AvailableUpdate.Should().BeNull();
        vm.UpdatesFoundCount.Should().Be(0);
        vm.ConfirmedUpdatesCount.Should().Be(0);
    }

    [WpfFact]
    public async Task UpdateAllAsync_clears_dedup_rows_when_master_install_fails_keeps_master_for_retry()
    {
        // Simulates the AMD chipset scenario: 3 device rows (master + 2 dedup'd) all
        // share the same vendor-installer:amd-chipset SourceUpdateId. The master
        // install fails; we still want the dedup'd rows to leave the Installable
        // filter (so the user does not see 17 ghost "more updates" rows), while the
        // master keeps its candidate so the user can retry it explicitly.
        var driverA = NewDriver("AMD Special Tools Driver", "ROOT\\SYSTEM\\0001", new Version(1, 0, 0, 0));
        var driverB = NewDriver("AMD Crash Defender", "ROOT\\SYSTEM\\0002", new Version(1, 0, 0, 0));
        var driverC = NewDriver("AMD Processor", "ROOT\\SYSTEM\\0003", new Version(1, 0, 0, 0));
        const string sharedId = "vendor-installer:amd-chipset:8.05";
        var candA = NewCandidate("ROOT\\SYSTEM\\0001", new Version(2, 0, 0, 0), UpdateInstallKind.VendorInstaller) with { SourceUpdateId = sharedId };
        var candB = NewCandidate("ROOT\\SYSTEM\\0002", new Version(2, 0, 0, 0), UpdateInstallKind.VendorInstaller) with { SourceUpdateId = sharedId };
        var candC = NewCandidate("ROOT\\SYSTEM\\0003", new Version(2, 0, 0, 0), UpdateInstallKind.VendorInstaller) with { SourceUpdateId = sharedId };
        var pipeline = new FailingInstallPipeline();
        var vm = new MainViewModel(
            new FakeScanService(new[] { driverA, driverB, driverC }),
            new[] { (IUpdateSource)new FakeUpdateSource(new[] { candA, candB, candC }) },
            new NullOemDetectionService(),
            pipeline,
            new ConfirmingInstallConfirmation(),
            new NullHistoryWindowOpener(),
            new NullSettingsWindowOpener(),
            new NullLogsWindowOpener(),
            NullLogger<MainViewModel>.Instance);

        await vm.ScanCommand.ExecuteAsync(null);
        await vm.UpdateAllCommand.ExecuteAsync(null);

        pipeline.Operations.Should().ContainSingle("only the master row should hit the pipeline; dedup'd rows are skipped");
        vm.Drivers[0].AvailableUpdate.Should().NotBeNull("the master keeps its candidate so the user can retry it");
        vm.Drivers[0].Status.Should().Be(DriverStatus.Error);
        vm.Drivers[1].AvailableUpdate.Should().BeNull("dedup'd rows leave the Installable filter once the install was attempted");
        vm.Drivers[2].AvailableUpdate.Should().BeNull("dedup'd rows leave the Installable filter once the install was attempted");
        vm.Drivers[1].Status.Should().Be(DriverStatus.Error, "the failure outcome is carried over so the user can see what happened");
        vm.Drivers[2].Status.Should().Be(DriverStatus.Error);
    }

    [WpfFact]
    public async Task UpdateAllAsync_runs_a_resolved_amd_chipset_package_only_once_for_shared_components()
    {
        var driverA = NewDriver("AMD Special Tools Driver", "ROOT\\SYSTEM\\0001", new Version(1, 0, 0, 0));
        var driverB = NewDriver("AMD I2C Controller", "ACPI\\AMDI0010", new Version(1, 0, 0, 0));
        var driverC = NewDriver("AMD SMBUS", "PCI\\VEN_1022&DEV_790B", new Version(1, 0, 0, 0));
        var chipsetPage = new Uri("https://www.amd.com/en/support/downloads/drivers.html");
        var candidates = new[]
        {
            NewCandidate(driverA.HardwareId, new Version(1, 7, 29, 0), UpdateInstallKind.VendorPage) with
            {
                SourceUpdateId = "ai-latest:ROOT\\SYSTEM\\0001",
                DownloadUrl = chipsetPage
            },
            NewCandidate(driverB.HardwareId, new Version(1, 2, 0, 126), UpdateInstallKind.VendorPage) with
            {
                SourceUpdateId = "ai-latest:ACPI\\AMDI0010",
                DownloadUrl = chipsetPage
            },
            NewCandidate(driverC.HardwareId, new Version(2, 0, 0, 26), UpdateInstallKind.VendorPage) with
            {
                SourceUpdateId = "ai-latest:PCI\\VEN_1022&DEV_790B",
                DownloadUrl = chipsetPage
            }
        };
        var pipeline = new ResolvingAmdChipsetPipeline();
        var vm = new MainViewModel(
            new FakeScanService(new[] { driverA, driverB, driverC }),
            new[] { (IUpdateSource)new FakeUpdateSource(candidates) },
            new NullOemDetectionService(),
            pipeline,
            new ConfirmingInstallConfirmation(),
            new NullHistoryWindowOpener(),
            new NullSettingsWindowOpener(),
            new NullLogsWindowOpener(),
            NullLogger<MainViewModel>.Instance);

        await vm.ScanCommand.ExecuteAsync(null);
        await vm.UpdateAllCommand.ExecuteAsync(null);

        pipeline.Operations.Should().ContainSingle();
        vm.Drivers.All(row => row.AvailableUpdate is null).Should().BeTrue();
    }

    [WpfFact]
    public async Task UpdateAllAsync_switches_filter_to_installable_and_scrolls_to_each_row()
    {
        var driverA = NewDriver("Intel Display", "PCI\\VEN_8086&DEV_4682", new Version(1, 0, 0, 0));
        var driverB = NewDriver("AMD Display", "PCI\\VEN_1002&DEV_747E", new Version(1, 0, 0, 0));
        var driverC = NewDriver("Realtek Audio", "PCI\\VEN_10EC&DEV_8168", new Version(1, 0, 0, 0));
        var candidateA = NewCandidate("PCI\\VEN_8086&DEV_4682", new Version(2, 0, 0, 0));
        var candidateB = NewCandidate("PCI\\VEN_1002&DEV_747E", new Version(2, 0, 0, 0));
        var candidateC = NewCandidate("PCI\\VEN_10EC&DEV_8168", new Version(2, 0, 0, 0));
        var vm = new MainViewModel(
            new FakeScanService(new[] { driverA, driverB, driverC }),
            new[] { (IUpdateSource)new FakeUpdateSource(new[] { candidateA, candidateB, candidateC }) },
            new NullOemDetectionService(),
            new SuccessfulInstallPipeline(),
            new ConfirmingInstallConfirmation(),
            new NullHistoryWindowOpener(),
            new NullSettingsWindowOpener(),
            new NullLogsWindowOpener(),
            NullLogger<MainViewModel>.Instance);

        var scrolledRows = new List<DriverRowViewModel>();
        vm.ScrollToRowRequested += (_, row) => scrolledRows.Add(row);

        vm.UpdateFilter = DriverUpdateFilter.AllDrivers;
        await vm.ScanCommand.ExecuteAsync(null);
        await vm.UpdateAllCommand.ExecuteAsync(null);

        vm.UpdateFilter.Should().Be(DriverUpdateFilter.UpdatesAvailable);
        scrolledRows.Should().HaveCount(3);
        scrolledRows.Select(r => r.DeviceName)
            .Should().BeEquivalentTo(new[] { "Intel Display", "AMD Display", "Realtek Audio" });
    }

    [WpfFact]
    public async Task UpdateAllAsync_runs_vendor_pages_through_pipeline_and_opens_page_on_skip()
    {
        var driver = NewDriver("NVIDIA Display", "PCI\\VEN_10DE&DEV_0001", new Version(1, 0, 0, 0));
        var candidate = NewCandidate(
            "PCI\\VEN_10DE&DEV_0001",
            new Version(2026, 5, 28, 0),
            UpdateInstallKind.VendorPage,
            UpdateConfidence.Advisory);
        var opener = new RecordingUpdatePageOpener();
        var pipeline = new RecordingInstallPipeline();
        var vm = new MainViewModel(
            new FakeScanService(new[] { driver }),
            new[] { (IUpdateSource)new FakeUpdateSource(new[] { candidate }) },
            new NullOemDetectionService(),
            pipeline,
            new ConfirmingInstallConfirmation(),
            new NullHistoryWindowOpener(),
            new NullSettingsWindowOpener(),
            new NullLogsWindowOpener(),
            NullLogger<MainViewModel>.Instance,
            opener);

        await vm.ScanCommand.ExecuteAsync(null);
        await vm.UpdateAllCommand.ExecuteAsync(null);

        pipeline.Operations.Should().ContainSingle()
            .Which.Candidate.SourceUpdateId.Should().Be(candidate.SourceUpdateId);
        opener.Opened.Should().ContainSingle().Which.Should().Be(candidate.DownloadUrl);
        vm.StatusText.Should().Contain("Opened 1 vendor pages");
        vm.ConfirmedUpdatesCount.Should().Be(0);
        vm.VendorChecksCount.Should().Be(1);
    }

    [WpfFact]
    public async Task UpdateAllAsync_does_not_open_vendor_page_when_pipeline_installs_it()
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
            new SuccessfulInstallPipeline(),
            new ConfirmingInstallConfirmation(),
            new NullHistoryWindowOpener(),
            new NullSettingsWindowOpener(),
            new NullLogsWindowOpener(),
            NullLogger<MainViewModel>.Instance,
            opener);

        await vm.ScanCommand.ExecuteAsync(null);
        await vm.UpdateAllCommand.ExecuteAsync(null);

        opener.Opened.Should().BeEmpty();
        vm.Drivers[0].AvailableUpdate.Should().BeNull();
        vm.Drivers[0].Status.Should().Be(DriverStatus.UpToDate);
        vm.StatusText.Should().Be("Install completed for 1 drivers.");
    }

    [WpfFact]
    public async Task UpdateAllAsync_installs_silent_and_opens_vendor_pages()
    {
        var installDriver = NewDriver("Intel Display", "PCI\\VEN_8086&DEV_4682", new Version(1, 0, 0, 0));
        var vendorDriver = NewDriver("NVIDIA Display", "PCI\\VEN_10DE&DEV_0001", new Version(1, 0, 0, 0));
        var installCandidate = NewCandidate("PCI\\VEN_8086&DEV_4682", new Version(2, 0, 0, 0));
        var advisory = NewCandidate(
            "PCI\\VEN_10DE&DEV_0001",
            new Version(2026, 5, 28, 0),
            UpdateInstallKind.VendorPage,
            UpdateConfidence.Advisory);
        var opener = new RecordingUpdatePageOpener();
        var pipeline = new RecordingInstallPipeline();
        var vm = new MainViewModel(
            new FakeScanService(new[] { installDriver, vendorDriver }),
            new[] { (IUpdateSource)new FakeUpdateSource(new[] { installCandidate, advisory }) },
            new NullOemDetectionService(),
            pipeline,
            new ConfirmingInstallConfirmation(),
            new NullHistoryWindowOpener(),
            new NullSettingsWindowOpener(),
            new NullLogsWindowOpener(),
            NullLogger<MainViewModel>.Instance,
            opener);

        await vm.ScanCommand.ExecuteAsync(null);
        await vm.UpdateAllCommand.ExecuteAsync(null);

        pipeline.Operations.Should().HaveCount(2);
        pipeline.Operations.Select(o => o.Candidate.SourceUpdateId)
            .Should().BeEquivalentTo(new[] { installCandidate.SourceUpdateId, advisory.SourceUpdateId });
        opener.Opened.Should().ContainSingle().Which.Should().Be(advisory.DownloadUrl);
        vm.StatusText.Should().Contain("Install completed for 2 drivers")
            .And.Contain("Opened 1 vendor pages");
    }

    [WpfFact]
    public async Task UpdateSelectedAsync_with_null_selection_writes_status_without_running()
    {
        var driver = NewDriver("Intel Display", "PCI\\VEN_8086&DEV_4682", new Version(1, 0, 0, 0));
        var candidate = NewCandidate("PCI\\VEN_8086&DEV_4682", new Version(2, 0, 0, 0));
        var vm = new MainViewModel(
            new FakeScanService(new[] { driver }),
            new[] { (IUpdateSource)new FakeUpdateSource(new[] { candidate }) },
            new NullOemDetectionService(),
            new ThrowingInstallPipeline(),
            new ThrowingInstallConfirmation(),
            new NullHistoryWindowOpener(),
            new NullSettingsWindowOpener(),
            new NullLogsWindowOpener(),
            NullLogger<MainViewModel>.Instance);

        await vm.ScanCommand.ExecuteAsync(null);
        await vm.UpdateSelectedCommand.ExecuteAsync(null);

        vm.StatusText.Should().Be("No rows selected.");
        vm.Drivers[0].Status.Should().Be(DriverStatus.Outdated);
    }

    [WpfFact]
    public async Task UpdateSelectedAsync_with_empty_selection_writes_status_without_running()
    {
        var driver = NewDriver("Intel Display", "PCI\\VEN_8086&DEV_4682", new Version(1, 0, 0, 0));
        var candidate = NewCandidate("PCI\\VEN_8086&DEV_4682", new Version(2, 0, 0, 0));
        var vm = new MainViewModel(
            new FakeScanService(new[] { driver }),
            new[] { (IUpdateSource)new FakeUpdateSource(new[] { candidate }) },
            new NullOemDetectionService(),
            new ThrowingInstallPipeline(),
            new ThrowingInstallConfirmation(),
            new NullHistoryWindowOpener(),
            new NullSettingsWindowOpener(),
            new NullLogsWindowOpener(),
            NullLogger<MainViewModel>.Instance);

        await vm.ScanCommand.ExecuteAsync(null);
        await vm.UpdateSelectedCommand.ExecuteAsync(new System.Collections.ArrayList());

        vm.StatusText.Should().Be("No rows selected.");
        vm.Drivers[0].Status.Should().Be(DriverStatus.Outdated);
    }

    [WpfFact]
    public async Task UpdateSelectedAsync_installs_only_provided_rows()
    {
        var driverA = NewDriver("Intel Display", "PCI\\VEN_8086&DEV_4682", new Version(1, 0, 0, 0));
        var driverB = NewDriver("AMD Display", "PCI\\VEN_1002&DEV_747E", new Version(1, 0, 0, 0));
        var candidateA = NewCandidate("PCI\\VEN_8086&DEV_4682", new Version(2, 0, 0, 0));
        var candidateB = NewCandidate("PCI\\VEN_1002&DEV_747E", new Version(2, 0, 0, 0));
        var pipeline = new RecordingInstallPipeline();
        var vm = new MainViewModel(
            new FakeScanService(new[] { driverA, driverB }),
            new[] { (IUpdateSource)new FakeUpdateSource(new[] { candidateA, candidateB }) },
            new NullOemDetectionService(),
            pipeline,
            new ConfirmingInstallConfirmation(),
            new NullHistoryWindowOpener(),
            new NullSettingsWindowOpener(),
            new NullLogsWindowOpener(),
            NullLogger<MainViewModel>.Instance);

        await vm.ScanCommand.ExecuteAsync(null);
        var selection = new System.Collections.ArrayList { vm.Drivers[1] };
        await vm.UpdateSelectedCommand.ExecuteAsync(selection);

        pipeline.Operations.Should().ContainSingle()
            .Which.Candidate.SourceUpdateId.Should().Be(candidateB.SourceUpdateId);
    }

    [WpfFact]
    public async Task UpdateSingleAsync_installs_only_that_row()
    {
        var driverA = NewDriver("Intel Display", "PCI\\VEN_8086&DEV_4682", new Version(1, 0, 0, 0));
        var driverB = NewDriver("AMD Display", "PCI\\VEN_1002&DEV_747E", new Version(1, 0, 0, 0));
        var candidateA = NewCandidate("PCI\\VEN_8086&DEV_4682", new Version(2, 0, 0, 0));
        var candidateB = NewCandidate("PCI\\VEN_1002&DEV_747E", new Version(2, 0, 0, 0));
        var pipeline = new RecordingInstallPipeline();
        var vm = new MainViewModel(
            new FakeScanService(new[] { driverA, driverB }),
            new[] { (IUpdateSource)new FakeUpdateSource(new[] { candidateA, candidateB }) },
            new NullOemDetectionService(),
            pipeline,
            new ConfirmingInstallConfirmation(),
            new NullHistoryWindowOpener(),
            new NullSettingsWindowOpener(),
            new NullLogsWindowOpener(),
            NullLogger<MainViewModel>.Instance);

        await vm.ScanCommand.ExecuteAsync(null);
        await vm.UpdateSingleCommand.ExecuteAsync(vm.Drivers[0]);

        pipeline.Operations.Should().ContainSingle()
            .Which.Candidate.SourceUpdateId.Should().Be(candidateA.SourceUpdateId);
    }

    [WpfFact]
    public async Task UpdateSingleAsync_with_vendor_page_tries_pipeline_then_opens_url()
    {
        var driver = NewDriver("NVIDIA Display", "PCI\\VEN_10DE&DEV_0001", new Version(1, 0, 0, 0));
        var advisory = NewCandidate(
            "PCI\\VEN_10DE&DEV_0001",
            new Version(2026, 5, 28, 0),
            UpdateInstallKind.VendorPage,
            UpdateConfidence.Advisory);
        var opener = new RecordingUpdatePageOpener();
        var pipeline = new RecordingInstallPipeline();
        var vm = new MainViewModel(
            new FakeScanService(new[] { driver }),
            new[] { (IUpdateSource)new FakeUpdateSource(new[] { advisory }) },
            new NullOemDetectionService(),
            pipeline,
            new ConfirmingInstallConfirmation(),
            new NullHistoryWindowOpener(),
            new NullSettingsWindowOpener(),
            new NullLogsWindowOpener(),
            NullLogger<MainViewModel>.Instance,
            opener);

        await vm.ScanCommand.ExecuteAsync(null);
        await vm.UpdateSingleCommand.ExecuteAsync(vm.Drivers[0]);

        pipeline.Operations.Should().ContainSingle()
            .Which.Candidate.SourceUpdateId.Should().Be(advisory.SourceUpdateId);
        opener.Opened.Should().ContainSingle()
            .Which.Should().Be(advisory.DownloadUrl);
        vm.StatusText.Should().Contain("Opened 1 vendor pages");
    }

    [WpfFact]
    public async Task OpenVendorChecksCommand_opens_vendor_pages()
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
            new NullLogsWindowOpener(),
            NullLogger<MainViewModel>.Instance,
            opener);

        await vm.ScanCommand.ExecuteAsync(null);
        vm.OpenVendorChecksCommand.Execute(null);

        opener.Opened.Should().ContainSingle()
            .Which.Should().Be(advisory.DownloadUrl);
        vm.ConfirmedUpdatesCount.Should().Be(1);
        vm.VendorChecksCount.Should().Be(1);
        vm.OpenVendorChecksCommand.CanExecute(null).Should().BeTrue();
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
            new NullLogsWindowOpener(),
            NullLogger<MainViewModel>.Instance,
            new RecordingUpdatePageOpener());

        vm.UpdateAllCommand.CanExecute(null).Should().BeFalse();
        vm.OpenVendorChecksCommand.CanExecute(null).Should().BeFalse();

        await vm.ScanCommand.ExecuteAsync(null);

        vm.UpdateAllCommand.CanExecute(null).Should().BeTrue();
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
    public async Task Update_filter_narrows_rows_with_and_without_available_updates()
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

        vm.UpdateFilter = DriverUpdateFilter.UpdatesAvailable;
        vm.DriversView.Cast<DriverRowViewModel>().Should().HaveCount(2);

        vm.UpdateFilter = DriverUpdateFilter.NoUpdateAvailable;
        vm.DriversView.Cast<DriverRowViewModel>().Should().ContainSingle(r => r.DeviceName == "Current Display");
    }

    [WpfFact]
    public async Task InitializeAsync_loads_drivers_from_cache_without_querying_update_sources()
    {
        var outdated = NewDriver("AMD Display", "PCI\\VEN_1002&DEV_747E", new Version(1, 0, 0, 0));
        var current = NewDriver("Realtek Audio", "PCI\\VEN_10EC&DEV_8168", new Version(2, 0, 0, 0));
        var candidate = NewCandidate("PCI\\VEN_1002&DEV_747E", new Version(2, 0, 0, 0));
        var snapshot = new DriverCacheSnapshot(new DateTimeOffset(2026, 5, 29, 22, 15, 0, TimeSpan.Zero), new[]
        {
            new CachedDriverEntry(outdated, DriverStatus.Outdated, candidate),
            new CachedDriverEntry(current, DriverStatus.UpToDate, null)
        });
        var counting = new CountingUpdateSource();
        var cache = new StubDriverCacheStore(snapshot);
        var vm = new MainViewModel(
            new FakeScanService(Array.Empty<DriverInfo>()),
            new[] { (IUpdateSource)counting },
            new NullOemDetectionService(),
            new NullInstallPipeline(),
            new NullInstallConfirmation(),
            new NullHistoryWindowOpener(),
            new NullSettingsWindowOpener(),
            new NullLogsWindowOpener(),
            NullLogger<MainViewModel>.Instance,
            driverCacheStore: cache);

        await vm.InitializeAsync();

        vm.Drivers.Should().HaveCount(2);
        vm.Drivers[0].AvailableUpdate.Should().NotBeNull();
        vm.Drivers[0].Status.Should().Be(DriverStatus.VerificationInconclusive);
        vm.Drivers[0].IsUpdateFromCache.Should().BeTrue();
        vm.Drivers[0].CanUpdate.Should().BeFalse();
        vm.Drivers[1].AvailableUpdate.Should().BeNull();
        vm.UpdatesFoundCount.Should().Be(0);
        vm.ScannedCount.Should().Be(2);
        vm.ProgressText.Should().Be("2 cached drivers (scan to refresh)");
        vm.UpdateAllCommand.CanExecute(null).Should().BeFalse();

        vm.UpdateFilter = DriverUpdateFilter.UpdatesAvailable;
        vm.DriversView.Cast<DriverRowViewModel>().Should().BeEmpty(
            "cached results require a fresh scan and cannot be updated");

        counting.SearchInvocations.Should().Be(0, "loading from cache must be instant and not hit update sources");
        vm.StatusText.Should().StartWith("Loaded 2 drivers from last scan");
    }

    [WpfFact]
    public async Task InitializeAsync_with_empty_cache_leaves_grid_empty()
    {
        var cache = new StubDriverCacheStore(null);
        var vm = new MainViewModel(
            new FakeScanService(Array.Empty<DriverInfo>()),
            new[] { (IUpdateSource)new CountingUpdateSource() },
            new NullOemDetectionService(),
            new NullInstallPipeline(),
            new NullInstallConfirmation(),
            new NullHistoryWindowOpener(),
            new NullSettingsWindowOpener(),
            new NullLogsWindowOpener(),
            NullLogger<MainViewModel>.Instance,
            driverCacheStore: cache);

        await vm.InitializeAsync();

        vm.Drivers.Should().BeEmpty();
        vm.ScannedCount.Should().Be(0);
    }

    [WpfFact]
    public async Task ScanAsync_saves_snapshot_to_cache()
    {
        var driverA = NewDriver("Intel Display", "PCI\\VEN_8086&DEV_4682", new Version(1, 0, 0, 0));
        var driverB = NewDriver("Realtek Audio", "PCI\\VEN_10EC&DEV_8168", new Version(2, 0, 0, 0));
        var candidate = NewCandidate("PCI\\VEN_8086&DEV_4682", new Version(2, 0, 0, 0));
        var cache = new StubDriverCacheStore(null);
        var vm = new MainViewModel(
            new FakeScanService(new[] { driverA, driverB }),
            new[] { (IUpdateSource)new FakeUpdateSource(new[] { candidate }) },
            new NullOemDetectionService(),
            new NullInstallPipeline(),
            new NullInstallConfirmation(),
            new NullHistoryWindowOpener(),
            new NullSettingsWindowOpener(),
            new NullLogsWindowOpener(),
            NullLogger<MainViewModel>.Instance,
            driverCacheStore: cache);

        await vm.ScanCommand.ExecuteAsync(null);

        cache.Saved.Should().ContainSingle();
        var saved = cache.Saved[0];
        saved.Entries.Should().HaveCount(2);
        saved.Entries.Should().ContainSingle(e => e.Driver.DeviceName == "Intel Display" && e.AvailableUpdate != null);
        saved.Entries.Should().ContainSingle(e => e.Driver.DeviceName == "Realtek Audio" && e.AvailableUpdate == null);
    }

    [WpfFact]
    public async Task ScanAsync_removes_cached_drivers_missing_from_current_inventory()
    {
        var scannedDriver = NewDriver("Intel Display", "PCI\\VEN_8086&DEV_4682", new Version(1, 0, 0, 0));
        var missingDriver = NewDriver("Realtek Audio", "PCI\\VEN_10EC&DEV_8168", new Version(1, 0, 0, 0));
        var pending = NewCandidate("PCI\\VEN_10EC&DEV_8168", new Version(2, 0, 0, 0));
        var cache = new StubDriverCacheStore(new DriverCacheSnapshot(DateTimeOffset.UtcNow, new[]
        {
            new CachedDriverEntry(scannedDriver, DriverStatus.UpToDate, null),
            new CachedDriverEntry(missingDriver, DriverStatus.Outdated, pending)
        }));
        var source = new RecordingDriverUpdateSource(Array.Empty<UpdateCandidate>());
        var vm = new MainViewModel(
            new FakeScanService(new[] { scannedDriver }),
            new[] { (IUpdateSource)source },
            new NullOemDetectionService(),
            new NullInstallPipeline(),
            new NullInstallConfirmation(),
            new NullHistoryWindowOpener(),
            new NullSettingsWindowOpener(),
            new NullLogsWindowOpener(),
            NullLogger<MainViewModel>.Instance,
            driverCacheStore: cache);

        await vm.InitializeAsync();
        await vm.ScanCommand.ExecuteAsync(null);

        vm.Drivers.Should().ContainSingle();
        vm.Drivers.Single().DeviceName.Should().Be("Intel Display");
        vm.ScannedCount.Should().Be(1);
        source.ReceivedDrivers.Should().ContainSingle()
            .Which.Select(driver => driver.DeviceName).Should().Equal("Intel Display");
        vm.UpdatesFoundCount.Should().Be(0);
        cache.Saved.Should().ContainSingle().Which.Entries.Should().ContainSingle()
            .Which.Driver.DeviceName.Should().Be("Intel Display");
    }

    [WpfFact]
    public async Task ScanAsync_does_not_accept_update_for_driver_missing_from_current_inventory()
    {
        var scannedDriver = NewDriver("Intel Display", "PCI\\VEN_8086&DEV_4682", new Version(1, 0, 0, 0));
        var missingDriver = NewDriver("Realtek Audio", "PCI\\VEN_10EC&DEV_8168", new Version(1, 0, 0, 0));
        var freshUpdate = NewCandidate("PCI\\VEN_10EC&DEV_8168", new Version(3, 0, 0, 0));
        var cache = new StubDriverCacheStore(new DriverCacheSnapshot(DateTimeOffset.UtcNow, new[]
        {
            new CachedDriverEntry(scannedDriver, DriverStatus.UpToDate, null),
            new CachedDriverEntry(missingDriver, DriverStatus.UpToDate, null)
        }));
        var source = new RecordingDriverUpdateSource(new[] { freshUpdate });
        var vm = new MainViewModel(
            new FakeScanService(new[] { scannedDriver }),
            new[] { (IUpdateSource)source },
            new NullOemDetectionService(),
            new NullInstallPipeline(),
            new NullInstallConfirmation(),
            new NullHistoryWindowOpener(),
            new NullSettingsWindowOpener(),
            new NullLogsWindowOpener(),
            NullLogger<MainViewModel>.Instance,
            driverCacheStore: cache);

        await vm.InitializeAsync();
        await vm.ScanCommand.ExecuteAsync(null);

        source.ReceivedDrivers.Should().ContainSingle()
            .Which.Select(driver => driver.DeviceName).Should().Equal("Intel Display");
        vm.Drivers.Should().ContainSingle();
        vm.Drivers.Single().DeviceName.Should().Be("Intel Display");
        vm.ScannedCount.Should().Be(1);
        vm.UpdatesFoundCount.Should().Be(0);
        cache.Saved.Should().ContainSingle().Which.Entries.Should().ContainSingle()
            .Which.Driver.DeviceName.Should().Be("Intel Display");
    }

    [WpfFact]
    public async Task ScanAsync_restores_pending_update_when_sources_return_nothing()
    {
        var driver = NewDriver("Intel Display", @"PCI\\VEN_8086&DEV_4682", new Version(1, 0, 0, 0));
        var pending = NewCandidate(@"PCI\\VEN_8086&DEV_4682", new Version(2, 0, 0, 0));
        var cache = new StubDriverCacheStore(new DriverCacheSnapshot(DateTimeOffset.UtcNow, new[]
        {
            new CachedDriverEntry(driver, DriverStatus.Outdated, pending)
        }));
        var vm = new MainViewModel(
            new FakeScanService(new[] { driver }),
            new[] { (IUpdateSource)new FakeUpdateSource(Array.Empty<UpdateCandidate>()) },
            new NullOemDetectionService(),
            new NullInstallPipeline(),
            new NullInstallConfirmation(),
            new NullHistoryWindowOpener(),
            new NullSettingsWindowOpener(),
            new NullLogsWindowOpener(),
            NullLogger<MainViewModel>.Instance,
            driverCacheStore: cache);

        await vm.InitializeAsync();
        await vm.ScanCommand.ExecuteAsync(null);

        var row = vm.Drivers.Single();
        row.AvailableUpdate.Should().NotBeNull();
        row.AvailableUpdate!.SourceUpdateId.Should().Be(pending.SourceUpdateId);
        row.IsUpdateFromCache.Should().BeTrue();
        row.Status.Should().Be(DriverStatus.VerificationInconclusive);
        row.CanUpdate.Should().BeFalse();
        vm.UpdatesFoundCount.Should().Be(0);
    }

    [WpfFact]
    public async Task Cached_update_cannot_be_installed_until_it_is_reverified()
    {
        var driver = NewDriver("Intel Display", "TEST_HWID", new Version(1, 0, 0, 0));
        var pending = NewCandidate("TEST_HWID", new Version(2, 0, 0, 0));
        var cache = new StubDriverCacheStore(new DriverCacheSnapshot(DateTimeOffset.UtcNow, new[]
        {
            new CachedDriverEntry(driver, DriverStatus.Outdated, pending)
        }));
        var pipeline = new RecordingInstallPipeline();
        var vm = new MainViewModel(
            new FakeScanService(Array.Empty<DriverInfo>()),
            Array.Empty<IUpdateSource>(),
            new NullOemDetectionService(),
            pipeline,
            new ConfirmingInstallConfirmation(),
            new NullHistoryWindowOpener(),
            new NullSettingsWindowOpener(),
            new NullLogsWindowOpener(),
            NullLogger<MainViewModel>.Instance,
            driverCacheStore: cache);

        await vm.InitializeAsync();
        vm.Drivers.Single().CanAskAi.Should().BeFalse();
        await vm.AskAiCommand.ExecuteAsync(vm.Drivers.Single());
        vm.StatusText.Should().Be("Run Scan before asking AI to review this driver.");

        await vm.UpdateSingleCommand.ExecuteAsync(vm.Drivers.Single());

        pipeline.Operations.Should().BeEmpty();
        vm.StatusText.Should().Be("No outdated drivers to update.");
    }

    [WpfFact]
    public async Task Clearing_cache_resets_the_main_view_and_prevents_old_results_from_returning()
    {
        var driver = NewDriver("Intel Display", "TEST_HWID", new Version(1, 0, 0, 0));
        var pending = NewCandidate("TEST_HWID", new Version(2, 0, 0, 0));
        var cache = new StubDriverCacheStore(new DriverCacheSnapshot(DateTimeOffset.UtcNow, new[]
        {
            new CachedDriverEntry(driver, DriverStatus.Outdated, pending)
        }));
        var vm = new MainViewModel(
            new FakeScanService(new[] { driver }),
            Array.Empty<IUpdateSource>(),
            new NullOemDetectionService(),
            new NullInstallPipeline(),
            new NullInstallConfirmation(),
            new NullHistoryWindowOpener(),
            new NullSettingsWindowOpener(),
            new NullLogsWindowOpener(),
            NullLogger<MainViewModel>.Instance,
            driverCacheStore: cache);
        await vm.InitializeAsync();
        vm.Drivers.Should().ContainSingle();

        await cache.ClearAsync();

        vm.Drivers.Should().BeEmpty();
        vm.ScannedCount.Should().Be(0);
        vm.UpdatesFoundCount.Should().Be(0);
        vm.StatusText.Should().Contain("search from scratch");

        await vm.ScanCommand.ExecuteAsync(null);

        vm.Drivers.Should().ContainSingle();
        vm.Drivers[0].AvailableUpdate.Should().BeNull();
        cache.Saved.Should().ContainSingle();
        cache.Saved[0].Entries[0].AvailableUpdate.Should().BeNull();
    }

    [WpfFact]
    public async Task ScanAsync_replaces_cached_update_when_source_offers_newer()
    {
        var driver = NewDriver("Intel Display", "PCI\\VEN_8086&DEV_4682", new Version(1, 0, 0, 0));
        var cachedPending = NewCandidate("PCI\\VEN_8086&DEV_4682", new Version(2, 0, 0, 0));
        var newer = NewCandidate("PCI\\VEN_8086&DEV_4682", new Version(3, 0, 0, 0));
        var cache = new StubDriverCacheStore(new DriverCacheSnapshot(DateTimeOffset.UtcNow, new[]
        {
            new CachedDriverEntry(driver, DriverStatus.Outdated, cachedPending)
        }));
        var vm = new MainViewModel(
            new FakeScanService(new[] { driver }),
            new[] { (IUpdateSource)new FakeUpdateSource(new[] { newer }) },
            new NullOemDetectionService(),
            new NullInstallPipeline(),
            new NullInstallConfirmation(),
            new NullHistoryWindowOpener(),
            new NullSettingsWindowOpener(),
            new NullLogsWindowOpener(),
            NullLogger<MainViewModel>.Instance,
            driverCacheStore: cache);

        await vm.InitializeAsync();
        await vm.ScanCommand.ExecuteAsync(null);

        var row = vm.Drivers.Single();
        row.AvailableUpdate!.NewVersion.Should().Be(new Version(3, 0, 0, 0));
        row.IsUpdateFromCache.Should().BeFalse();
        cache.Saved.Should().ContainSingle();
        cache.Saved[0].Entries.Should().ContainSingle();
        cache.Saved[0].Entries[0].AvailableUpdate!.NewVersion.Should().Be(new Version(3, 0, 0, 0));
        cache.Saved[0].Entries[0].AvailableUpdate!.SourceUpdateId
            .Should().Be(newer.SourceUpdateId);
    }

    [WpfFact]
    public async Task ScanAsync_drops_cached_update_when_installed_driver_caught_up()
    {
        var oldDriver = NewDriver("Intel Display", "PCI\\VEN_8086&DEV_4682", new Version(1, 0, 0, 0));
        var pending = NewCandidate("PCI\\VEN_8086&DEV_4682", new Version(2, 0, 0, 0));
        var updatedDriver = NewDriver("Intel Display", "PCI\\VEN_8086&DEV_4682", new Version(2, 0, 0, 0));
        var cache = new StubDriverCacheStore(new DriverCacheSnapshot(DateTimeOffset.UtcNow, new[]
        {
            new CachedDriverEntry(oldDriver, DriverStatus.Outdated, pending)
        }));
        var vm = new MainViewModel(
            new FakeScanService(new[] { updatedDriver }),
            new[] { (IUpdateSource)new FakeUpdateSource(Array.Empty<UpdateCandidate>()) },
            new NullOemDetectionService(),
            new NullInstallPipeline(),
            new NullInstallConfirmation(),
            new NullHistoryWindowOpener(),
            new NullSettingsWindowOpener(),
            new NullLogsWindowOpener(),
            NullLogger<MainViewModel>.Instance,
            driverCacheStore: cache);

        await vm.InitializeAsync();
        await vm.ScanCommand.ExecuteAsync(null);

        vm.Drivers.Single().AvailableUpdate.Should().BeNull();
    }

    [WpfFact]
    public async Task UpdateSingleAsync_on_vendor_check_row_shows_manual_action_after_opening_page()
    {
        var driver = NewDriver("AMD Processor", "PCI\\VEN_1022&DEV_0001", new Version(1, 0, 0, 0));
        var advisory = NewCandidate(
            "PCI\\VEN_1022&DEV_0001",
            new Version(2026, 5, 18, 0),
            UpdateInstallKind.VendorPage,
            UpdateConfidence.Advisory);
        var opener = new RecordingUpdatePageOpener();
        var pipeline = new RecordingInstallPipeline();
        var vm = new MainViewModel(
            new FakeScanService(new[] { driver }),
            new[] { (IUpdateSource)new FakeUpdateSource(new[] { advisory }) },
            new NullOemDetectionService(),
            pipeline,
            new ConfirmingInstallConfirmation(),
            new NullHistoryWindowOpener(),
            new NullSettingsWindowOpener(),
            new NullLogsWindowOpener(),
            NullLogger<MainViewModel>.Instance,
            opener);

        await vm.ScanCommand.ExecuteAsync(null);
        vm.Drivers[0].Status = DriverStatus.UpToDate;

        await vm.UpdateSingleCommand.ExecuteAsync(vm.Drivers[0]);

        pipeline.Operations.Should().ContainSingle()
            .Which.Candidate.SourceUpdateId.Should().Be(advisory.SourceUpdateId);
        opener.Opened.Should().ContainSingle().Which.Should().Be(advisory.DownloadUrl);
        vm.Drivers[0].Status.Should().Be(DriverStatus.ManualActionRequired);
    }

    private sealed class StubDriverCacheStore : IDriverCacheStore
    {
        private DriverCacheSnapshot? _snapshot;

        public event EventHandler? Cleared;

        public StubDriverCacheStore(DriverCacheSnapshot? snapshot)
        {
            _snapshot = snapshot;
        }

        public List<DriverCacheSnapshot> Saved { get; } = new();

        public Task<DriverCacheSnapshot?> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_snapshot);

        public Task SaveAsync(DriverCacheSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            Saved.Add(snapshot);
            _snapshot = snapshot;
            return Task.CompletedTask;
        }

        public Task<int> ClearAsync(CancellationToken cancellationToken = default)
        {
            var count = _snapshot?.Entries.Count(entry => entry.AvailableUpdate is not null) ?? 0;
            _snapshot = null;
            Cleared?.Invoke(this, EventArgs.Empty);
            return Task.FromResult(count);
        }
    }

    private sealed class CountingUpdateSource : IUpdateSource
    {
        public int SearchInvocations { get; private set; }
        public UpdateSource Kind => UpdateSource.WindowsUpdate;
        public string DisplayName => "Counting";

        public async IAsyncEnumerable<UpdateCandidate> SearchAsync(
            IReadOnlyCollection<DriverInfo> drivers,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            SearchInvocations++;
            await Task.Yield();
            yield break;
        }
    }

    private sealed class RecordingDriverUpdateSource : IUpdateSource
    {
        private readonly IReadOnlyList<UpdateCandidate> _candidates;

        public RecordingDriverUpdateSource(IReadOnlyList<UpdateCandidate> candidates)
        {
            _candidates = candidates;
        }

        public List<IReadOnlyCollection<DriverInfo>> ReceivedDrivers { get; } = new();
        public UpdateSource Kind => UpdateSource.WindowsUpdate;
        public string DisplayName => "Recording";

        public async IAsyncEnumerable<UpdateCandidate> SearchAsync(
            IReadOnlyCollection<DriverInfo> drivers,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ReceivedDrivers.Add(drivers);
            foreach (var candidate in _candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return candidate;
            }
        }
    }

    [WpfFact]
    public async Task ScanAsync_skips_windows_update_source_when_disabled_in_settings()
    {
        var driver = NewDriver("Intel Display", "PCI\\VEN_8086&DEV_4682", new Version(1, 0, 0, 0));
        var candidate = NewCandidate("PCI\\VEN_8086&DEV_4682", new Version(2, 0, 0, 0));
        var source = new CountingUpdateSource();

        var vm = new MainViewModel(
            new FakeScanService(new[] { driver }),
            new[] { (IUpdateSource)source },
            new NullOemDetectionService(),
            new NullInstallPipeline(),
            new NullInstallConfirmation(),
            new NullHistoryWindowOpener(),
            new NullSettingsWindowOpener(),
            new NullLogsWindowOpener(),
            NullLogger<MainViewModel>.Instance,
            updaterSettings: new StubOptionsMonitor<UpdaterSettings>(
                new UpdaterSettings { WindowsUpdateEnabled = false }));

        await vm.ScanCommand.ExecuteAsync(null);

        source.SearchInvocations.Should().Be(0);
        vm.Drivers[0].AvailableUpdate.Should().BeNull();
        vm.UpdatesFoundCount.Should().Be(0);
    }

    [WpfFact]
    public async Task ScanAsync_skips_oem_sources_when_disabled_in_settings()
    {
        var driver = NewDriver("AMD Display", "PCI\\VEN_1002&DEV_747E", new Version(1, 0, 0, 0));
        var candidate = NewCandidate("PCI\\VEN_1002&DEV_747E", new Version(2, 0, 0, 0));
        var oemSource = new FakeOemUpdateSource(new[] { candidate });

        var vm = new MainViewModel(
            new FakeScanService(new[] { driver }),
            new[] { (IUpdateSource)oemSource },
            new NullOemDetectionService(),
            new NullInstallPipeline(),
            new NullInstallConfirmation(),
            new NullHistoryWindowOpener(),
            new NullSettingsWindowOpener(),
            new NullLogsWindowOpener(),
            NullLogger<MainViewModel>.Instance,
            updaterSettings: new StubOptionsMonitor<UpdaterSettings>(
                new UpdaterSettings { OemSourcesEnabled = false }));

        await vm.ScanCommand.ExecuteAsync(null);

        vm.Drivers[0].AvailableUpdate.Should().BeNull();
        vm.UpdatesFoundCount.Should().Be(0);
    }

    [WpfFact]
    public async Task ScanAsync_queries_sources_when_settings_enable_them()
    {
        var driver = NewDriver("Intel Display", "PCI\\VEN_8086&DEV_4682", new Version(1, 0, 0, 0));
        var candidate = NewCandidate("PCI\\VEN_8086&DEV_4682", new Version(2, 0, 0, 0));

        var vm = new MainViewModel(
            new FakeScanService(new[] { driver }),
            new[] { (IUpdateSource)new FakeUpdateSource(new[] { candidate }) },
            new NullOemDetectionService(),
            new NullInstallPipeline(),
            new NullInstallConfirmation(),
            new NullHistoryWindowOpener(),
            new NullSettingsWindowOpener(),
            new NullLogsWindowOpener(),
            NullLogger<MainViewModel>.Instance,
            updaterSettings: new StubOptionsMonitor<UpdaterSettings>(new UpdaterSettings()));

        await vm.ScanCommand.ExecuteAsync(null);

        vm.Drivers[0].AvailableUpdate.Should().NotBeNull();
        vm.UpdatesFoundCount.Should().Be(1);
    }

    private sealed class FakeOemUpdateSource : IUpdateSource
    {
        private readonly IEnumerable<UpdateCandidate> _candidates;

        public FakeOemUpdateSource(IEnumerable<UpdateCandidate> candidates)
        {
            _candidates = candidates;
        }

        public UpdateSource Kind => UpdateSource.Oem;
        public string DisplayName => "Fake OEM";

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

    private sealed class StubOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public StubOptionsMonitor(T value) { CurrentValue = value; }
        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
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
            new NullLogsWindowOpener(),
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

    private sealed class RecordingInstallPipeline : IInstallPipeline
    {
        public List<UpdateOperation> Operations { get; } = new();

        public Task<UpdateOperation> ExecuteAsync(
            UpdateOperation operation,
            InstallOptions options,
            IProgress<UpdateOperation>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Operations.Add(operation);
            // Mirrors the real pipeline: a vendor page candidate that could not be
            // resolved to a direct installer is skipped so the UI opens the page.
            var finished = operation.Candidate.InstallKind == UpdateInstallKind.VendorPage
                ? operation with
                {
                    Status = UpdateStatus.Skipped,
                    ErrorMessage = $"Open the official vendor page to install this update: {operation.Candidate.DownloadUrl}",
                    CompletedAt = DateTimeOffset.UtcNow
                }
                : operation with
                {
                    Status = UpdateStatus.Succeeded,
                    CompletedAt = DateTimeOffset.UtcNow
                };
            progress?.Report(finished);
            return Task.FromResult(finished);
        }
    }

    private sealed class ResolvingAmdChipsetPipeline : IInstallPipeline
    {
        public List<UpdateOperation> Operations { get; } = new();

        public Task<UpdateOperation> ExecuteAsync(
            UpdateOperation operation,
            InstallOptions options,
            IProgress<UpdateOperation>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Operations.Add(operation);
            var finished = operation with
            {
                Candidate = operation.Candidate with
                {
                    InstallKind = UpdateInstallKind.VendorInstaller,
                    SourceUpdateId = $"vendor-installer:amd-chipset:resolved:{operation.Candidate.SourceUpdateId}",
                    DownloadUrl = new Uri("https://drivers.amd.com/drivers/amd_chipset_software.exe")
                },
                Status = UpdateStatus.Succeeded,
                CompletedAt = DateTimeOffset.UtcNow
            };
            progress?.Report(finished);
            return Task.FromResult(finished);
        }
    }

    private sealed class FailingInstallPipeline : IInstallPipeline
    {
        public List<UpdateOperation> Operations { get; } = new();

        public Task<UpdateOperation> ExecuteAsync(
            UpdateOperation operation,
            InstallOptions options,
            IProgress<UpdateOperation>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Operations.Add(operation);
            var finished = operation with
            {
                Status = UpdateStatus.Failed,
                ErrorMessage = "Vendor installer exit 2: ",
                CompletedAt = DateTimeOffset.UtcNow
            };
            progress?.Report(finished);
            return Task.FromResult(finished);
        }
    }

    private sealed class ThrowingInstallConfirmation : IInstallConfirmation
    {
        public InstallOptions? Confirm(UpdateOperation operation, bool dryRun) =>
            throw new InvalidOperationException("confirmation should not be called");
    }
}
