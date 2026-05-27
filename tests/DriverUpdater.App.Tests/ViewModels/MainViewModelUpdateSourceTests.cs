using System.Runtime.CompilerServices;
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

    private static UpdateCandidate NewCandidate(string hardwareId, Version newVersion) => new(
        ForHardwareId: hardwareId,
        Source: UpdateSource.WindowsUpdate,
        NewVersion: newVersion,
        NewDate: new DateOnly(2026, 1, 1),
        DownloadUrl: new Uri("https://example.com/x.cab"),
        SizeBytes: 1024,
        KbArticle: null,
        IsSuperseded: false,
        SourceUpdateId: Guid.NewGuid().ToString(),
        SupersededIds: Array.Empty<string>());

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
}
