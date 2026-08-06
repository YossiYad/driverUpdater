using DriverUpdater.App.ViewModels;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriverUpdater.App.Tests.ViewModels;

public class ExcludedDriverSelectionViewModelTests
{
    [WpfFact]
    public async Task LoadAsync_lists_the_scanned_drivers_and_ticks_the_excluded_ones()
    {
        var store = new FakeExclusionStore(@"ID\gpu");
        var vm = NewViewModel(store, Cache(Driver(@"ID\gpu", "AMD GPU"), Driver(@"ID\nic", "Realtek NIC")));

        await vm.LoadAsync();

        vm.Drivers.Should().HaveCount(2);
        vm.Drivers.Single(d => d.DeviceId == @"ID\gpu").IsExcluded.Should().BeTrue();
        vm.Drivers.Single(d => d.DeviceId == @"ID\nic").IsExcluded.Should().BeFalse();
        vm.ExcludedCount.Should().Be(1);
    }

    [WpfFact]
    public async Task Ticking_a_driver_writes_nothing_until_save()
    {
        var store = new FakeExclusionStore();
        var vm = NewViewModel(store, Cache(Driver(@"ID\gpu", "AMD GPU")));
        await vm.LoadAsync();

        vm.Drivers[0].IsExcluded = true;

        store.Saved.Should().BeNull("the tick is only a pending choice until Save");
        vm.ExcludedCount.Should().Be(1);

        await vm.SaveCommand.ExecuteAsync(null);

        store.Saved!.DeviceIds.Should().Equal(@"ID\gpu");
    }

    [WpfFact]
    public async Task Unticking_a_driver_and_saving_puts_it_back_under_updates()
    {
        var store = new FakeExclusionStore(@"ID\gpu");
        var vm = NewViewModel(store, Cache(Driver(@"ID\gpu", "AMD GPU")));
        await vm.LoadAsync();

        vm.Drivers[0].IsExcluded = false;
        await vm.SaveCommand.ExecuteAsync(null);

        store.Saved!.DeviceIds.Should().BeEmpty();
        vm.Drivers.Should().ContainSingle("the device is still installed, so it can be excluded again");
    }

    [WpfFact]
    public async Task Unticking_an_excluded_device_that_is_gone_drops_it_from_the_file()
    {
        var store = new FakeExclusionStore(@"ID\unplugged");
        var vm = NewViewModel(store, Cache(Driver(@"ID\gpu", "AMD GPU")));
        await vm.LoadAsync();

        var stale = vm.Drivers.Single(d => d.DeviceId == @"ID\unplugged");
        stale.IsFromLastScan.Should().BeFalse();
        stale.IsExcluded.Should().BeTrue();

        stale.IsExcluded = false;
        await vm.SaveCommand.ExecuteAsync(null);

        store.Saved!.DeviceIds.Should().BeEmpty();
    }

    [WpfFact]
    public async Task Saving_raises_the_event_that_closes_the_window()
    {
        var vm = NewViewModel(new FakeExclusionStore(), Cache(Driver(@"ID\gpu", "AMD GPU")));
        await vm.LoadAsync();
        var closed = 0;
        vm.SaveCompleted += (_, _) => closed++;

        await vm.SaveCommand.ExecuteAsync(null);

        closed.Should().Be(1);
    }

    [WpfFact]
    public async Task A_failed_save_keeps_the_window_open_and_says_why()
    {
        var vm = NewViewModel(new ThrowingExclusionStore(), Cache(Driver(@"ID\gpu", "AMD GPU")));
        await vm.LoadAsync();
        var closed = 0;
        vm.SaveCompleted += (_, _) => closed++;

        await vm.SaveCommand.ExecuteAsync(null);

        closed.Should().Be(0);
        vm.StatusText.Should().Contain("Could not save");
    }

    [WpfFact]
    public async Task Without_a_scan_the_list_is_empty_and_says_so()
    {
        var vm = NewViewModel(new FakeExclusionStore(), cache: null);

        await vm.LoadAsync();

        vm.HasDrivers.Should().BeFalse();
        vm.HasNoDrivers.Should().BeTrue();
        vm.StatusText.Should().Contain("Run a scan");
    }

    [WpfFact]
    public async Task A_failed_load_is_reported_instead_of_looking_like_an_empty_scan()
    {
        var vm = NewViewModel(
            new UnreadableExclusionStore(),
            Cache(Driver(@"ID\gpu", "AMD GPU")));

        await vm.LoadAsync();

        vm.HasLoaded.Should().BeFalse();
        vm.HasNoDrivers.Should().BeFalse();
        vm.SaveCommand.CanExecute(null).Should().BeFalse(
            "saving an uninitialized list would erase the user's exclusions");
        vm.StatusText.Should().Contain("Could not load").And.Contain("access denied");
    }

    [WpfFact]
    public async Task The_search_box_filters_the_list()
    {
        var vm = NewViewModel(
            new FakeExclusionStore(),
            Cache(Driver(@"ID\gpu", "AMD Radeon"), Driver(@"ID\nic", "Realtek NIC")));
        await vm.LoadAsync();

        vm.SearchText = "radeon";

        vm.DriversView.Cast<ExcludedDriverRowViewModel>()
            .Should().ContainSingle().Which.DeviceName.Should().Be("AMD Radeon");
    }

    private static ExcludedDriverSelectionViewModel NewViewModel(
        IDriverUpdateExclusionStore store,
        DriverCacheSnapshot? cache) =>
        new(store, NullLogger<ExcludedDriverSelectionViewModel>.Instance, new FakeCacheStore(cache));

    private static DriverCacheSnapshot Cache(params DriverInfo[] drivers) =>
        new(DateTimeOffset.UtcNow, drivers.Select(d => new CachedDriverEntry(d, DriverStatus.UpToDate, null)).ToArray());

    private static DriverInfo Driver(string deviceId, string name) => new(
        DeviceId: deviceId,
        HardwareId: @"PCI\VEN_0000",
        DeviceName: name,
        Category: DriverCategory.Display,
        Provider: "Test",
        Manufacturer: "Test",
        CurrentVersion: new Version(1, 0, 0, 0),
        CurrentDate: new DateOnly(2024, 1, 1),
        InfName: "oem.inf",
        InfPath: null,
        IsSigned: true,
        DeviceClass: "Display");

    private sealed class FakeExclusionStore : IDriverUpdateExclusionStore
    {
        private readonly DriverUpdateExclusions _initial;

        public FakeExclusionStore(params string[] deviceIds) => _initial = new DriverUpdateExclusions(deviceIds);

        public DriverUpdateExclusions? Saved { get; private set; }

        public Task<DriverUpdateExclusions> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Saved ?? _initial);

        public Task SaveAsync(DriverUpdateExclusions exclusions, CancellationToken cancellationToken = default)
        {
            Saved = exclusions;
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingExclusionStore : IDriverUpdateExclusionStore
    {
        public Task<DriverUpdateExclusions> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(DriverUpdateExclusions.Empty);

        public Task SaveAsync(DriverUpdateExclusions exclusions, CancellationToken cancellationToken = default) =>
            throw new System.IO.IOException("disk is full");
    }

    private sealed class UnreadableExclusionStore : IDriverUpdateExclusionStore
    {
        public Task<DriverUpdateExclusions> LoadAsync(CancellationToken cancellationToken = default) =>
            throw new System.IO.IOException("access denied");

        public Task SaveAsync(DriverUpdateExclusions exclusions, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeCacheStore : IDriverCacheStore
    {
        private readonly DriverCacheSnapshot? _snapshot;

        public FakeCacheStore(DriverCacheSnapshot? snapshot) => _snapshot = snapshot;

        public event EventHandler? Cleared;

        public Task<DriverCacheSnapshot?> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_snapshot);

        public Task SaveAsync(DriverCacheSnapshot snapshot, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<int> ClearAsync(CancellationToken cancellationToken = default)
        {
            Cleared?.Invoke(this, EventArgs.Empty);
            return Task.FromResult(0);
        }
    }
}
