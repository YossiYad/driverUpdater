using System.Runtime.CompilerServices;
using DriverUpdater.App.Tests.Stubs;
using DriverUpdater.App.ViewModels;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using DriverUpdater.Core.Options;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DriverUpdater.App.Tests.ViewModels;

public class MainViewModelAutoUpdateSelectionTests
{
    [WpfFact]
    public async Task Ticking_a_row_persists_that_device_as_selected_for_automatic_updates()
    {
        var driver = NewDriver("Intel Display", "PCI\\VEN_8086&DEV_4682");
        var store = new FakeAutoUpdateSelectionStore();
        var vm = NewVm(new[] { driver }, store);
        await vm.ScanCommand.ExecuteAsync(null);

        vm.Drivers[0].IsAutoUpdateEnabled = true;

        store.Saved.Should().ContainSingle()
            .Which.DeviceIds.Should().BeEquivalentTo(driver.DeviceId);
    }

    [WpfFact]
    public async Task Unticking_a_row_removes_that_device_from_the_saved_selection()
    {
        var driver = NewDriver("Intel Display", "PCI\\VEN_8086&DEV_4682");
        var store = new FakeAutoUpdateSelectionStore(driver.DeviceId);
        var vm = NewVm(new[] { driver }, store);
        await vm.InitializeAsync();
        await vm.ScanCommand.ExecuteAsync(null);

        vm.Drivers[0].IsAutoUpdateEnabled.Should().BeTrue("the saved selection is restored onto rescanned rows");

        vm.Drivers[0].IsAutoUpdateEnabled = false;

        store.Saved.Should().ContainSingle().Which.DeviceIds.Should().BeEmpty();
    }

    [WpfFact]
    public async Task Scan_restores_the_selection_onto_the_rows_it_creates()
    {
        var selected = NewDriver("Intel Display", "PCI\\VEN_8086&DEV_4682");
        var other = NewDriver("Realtek Audio", "PCI\\VEN_10EC&DEV_8168");
        var store = new FakeAutoUpdateSelectionStore(selected.DeviceId);
        var vm = NewVm(new[] { selected, other }, store);

        await vm.InitializeAsync();
        await vm.ScanCommand.ExecuteAsync(null);

        vm.Drivers.Single(row => row.DeviceName == "Intel Display").IsAutoUpdateEnabled.Should().BeTrue();
        vm.Drivers.Single(row => row.DeviceName == "Realtek Audio").IsAutoUpdateEnabled.Should().BeFalse();
        store.Saved.Should().BeEmpty("restoring the selection is not itself a user change");
    }

    [WpfFact]
    public void Selection_is_only_active_while_the_schedule_limits_updates_to_chosen_drivers()
    {
        var driver = NewDriver("Intel Display", "PCI\\VEN_8086&DEV_4682");

        NewVm(new[] { driver }, new FakeAutoUpdateSelectionStore())
            .IsAutoUpdateSelectionActive.Should().BeFalse();

        NewVm(
            new[] { driver },
            new FakeAutoUpdateSelectionStore(),
            new ScheduleSettings { AutoUpdateScope = AutoUpdateScope.SelectedDrivers })
            .IsAutoUpdateSelectionActive.Should().BeTrue();
    }

    private static MainViewModel NewVm(
        IEnumerable<DriverInfo> drivers,
        IAutoUpdateSelectionStore selectionStore,
        ScheduleSettings? schedule = null) =>
        new(new FakeScanService(drivers),
            Array.Empty<IUpdateSource>(),
            new NullOemDetectionService(),
            new NullInstallPipeline(),
            new NullInstallConfirmation(),
            new NullHistoryWindowOpener(),
            new NullSettingsWindowOpener(),
            new NullLogsWindowOpener(),
            NullLogger<MainViewModel>.Instance,
            autoUpdateSelectionStore: selectionStore,
            scheduleSettings: new StubOptionsMonitor<ScheduleSettings>(schedule ?? new ScheduleSettings()));

    private static DriverInfo NewDriver(string name, string hardwareId) => new(
        DeviceId: $"{hardwareId}\\INSTANCE",
        HardwareId: hardwareId,
        DeviceName: name,
        Category: DriverCategory.Display,
        Provider: "TestProvider",
        Manufacturer: "TestMaker",
        CurrentVersion: new Version(1, 0, 0, 0),
        CurrentDate: new DateOnly(2024, 1, 1),
        InfName: "oem.inf",
        InfPath: null,
        IsSigned: true,
        DeviceClass: "Display");

    private sealed class FakeAutoUpdateSelectionStore : IAutoUpdateSelectionStore
    {
        private AutoUpdateSelection _selection;

        public FakeAutoUpdateSelectionStore(params string[] deviceIds) =>
            _selection = new AutoUpdateSelection(deviceIds);

        public List<AutoUpdateSelection> Saved { get; } = new();

        public Task<AutoUpdateSelection> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_selection);

        public Task SaveAsync(AutoUpdateSelection selection, CancellationToken cancellationToken = default)
        {
            _selection = selection;
            Saved.Add(selection);
            return Task.CompletedTask;
        }
    }

    private sealed class StubOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public StubOptionsMonitor(T value) => CurrentValue = value;

        public T CurrentValue { get; }

        public T Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
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
}
