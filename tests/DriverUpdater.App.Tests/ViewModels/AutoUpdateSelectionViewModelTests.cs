using DriverUpdater.App.ViewModels;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriverUpdater.App.Tests.ViewModels;

public class AutoUpdateSelectionViewModelTests
{
    [WpfFact]
    public async Task LoadAsync_lists_the_scanned_drivers_and_ticks_the_chosen_ones()
    {
        var store = new FakeSelectionStore(@"ID\gpu");
        var vm = NewViewModel(store, Cache(Driver(@"ID\gpu", "AMD GPU"), Driver(@"ID\nic", "Realtek NIC")));

        await vm.LoadAsync();

        vm.Drivers.Should().HaveCount(2);
        vm.Drivers.Single(d => d.DeviceId == @"ID\gpu").IsSelected.Should().BeTrue();
        vm.Drivers.Single(d => d.DeviceId == @"ID\nic").IsSelected.Should().BeFalse();
        vm.SelectedCount.Should().Be(1);
    }

    [WpfFact]
    public async Task Ticking_a_driver_saves_the_selection_immediately()
    {
        var store = new FakeSelectionStore();
        var vm = NewViewModel(store, Cache(Driver(@"ID\gpu", "AMD GPU")));
        await vm.LoadAsync();

        vm.Drivers[0].IsSelected = true;

        store.Saved.Should().NotBeNull();
        store.Saved!.DeviceIds.Should().Equal(@"ID\gpu");
    }

    [WpfFact]
    public async Task Removing_a_scanned_driver_unticks_it_but_keeps_it_on_the_list()
    {
        var store = new FakeSelectionStore(@"ID\gpu");
        var vm = NewViewModel(store, Cache(Driver(@"ID\gpu", "AMD GPU")));
        await vm.LoadAsync();

        await vm.RemoveCommand.ExecuteAsync(vm.Drivers[0]);

        vm.Drivers.Should().ContainSingle("the device is still installed, so it can be picked again");
        vm.Drivers[0].IsSelected.Should().BeFalse();
        store.Saved!.DeviceIds.Should().BeEmpty();
    }

    [WpfFact]
    public async Task A_chosen_device_that_is_gone_is_listed_so_it_can_be_removed()
    {
        var store = new FakeSelectionStore(@"ID\unplugged");
        var vm = NewViewModel(store, Cache(Driver(@"ID\gpu", "AMD GPU")));
        await vm.LoadAsync();

        var stale = vm.Drivers.Single(d => d.DeviceId == @"ID\unplugged");
        stale.IsFromLastScan.Should().BeFalse();
        stale.IsSelected.Should().BeTrue();

        await vm.RemoveCommand.ExecuteAsync(stale);

        vm.Drivers.Should().ContainSingle().Which.DeviceId.Should().Be(@"ID\gpu");
        store.Saved!.DeviceIds.Should().BeEmpty();
    }

    [WpfFact]
    public async Task Remove_all_clears_every_choice()
    {
        var store = new FakeSelectionStore(@"ID\gpu", @"ID\gone");
        var vm = NewViewModel(store, Cache(Driver(@"ID\gpu", "AMD GPU"), Driver(@"ID\nic", "Realtek NIC")));
        await vm.LoadAsync();

        await vm.ClearAllCommand.ExecuteAsync(null);

        store.Saved!.DeviceIds.Should().BeEmpty();
        vm.Drivers.Should().OnlyContain(d => !d.IsSelected);
        vm.Drivers.Should().HaveCount(2, "the stale entry is gone, the scanned ones stay pickable");
    }

    [WpfFact]
    public async Task Without_a_scan_the_list_is_empty_and_says_so()
    {
        var vm = NewViewModel(new FakeSelectionStore(), cache: null);

        await vm.LoadAsync();

        vm.HasDrivers.Should().BeFalse();
        vm.HasNoDrivers.Should().BeTrue();
        vm.StatusText.Should().Contain("Run a scan");
    }

    [WpfFact]
    public async Task The_search_box_filters_the_list()
    {
        var vm = NewViewModel(
            new FakeSelectionStore(),
            Cache(Driver(@"ID\gpu", "AMD Radeon"), Driver(@"ID\nic", "Realtek NIC")));
        await vm.LoadAsync();

        vm.SearchText = "radeon";

        vm.DriversView.Cast<AutoUpdateDriverRowViewModel>()
            .Should().ContainSingle().Which.DeviceName.Should().Be("AMD Radeon");
    }

    private static AutoUpdateSelectionViewModel NewViewModel(
        IAutoUpdateSelectionStore store,
        DriverCacheSnapshot? cache) =>
        new(store, NullLogger<AutoUpdateSelectionViewModel>.Instance, new FakeCacheStore(cache));

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

    private sealed class FakeSelectionStore : IAutoUpdateSelectionStore
    {
        private readonly AutoUpdateSelection _initial;

        public FakeSelectionStore(params string[] deviceIds) => _initial = new AutoUpdateSelection(deviceIds);

        public AutoUpdateSelection? Saved { get; private set; }

        public Task<AutoUpdateSelection> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Saved ?? _initial);

        public Task SaveAsync(AutoUpdateSelection selection, CancellationToken cancellationToken = default)
        {
            Saved = selection;
            return Task.CompletedTask;
        }
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
