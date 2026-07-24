using System.Runtime.CompilerServices;
using DriverUpdater.App.Tests.Stubs;
using DriverUpdater.App.ViewModels;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriverUpdater.App.Tests.ViewModels;

public class MainViewModelTests
{
    [WpfFact]
    public async Task ScanAsync_populates_drivers_collection()
    {
        var vm = NewVm(NewDriver("A", DriverCategory.Display), NewDriver("B", DriverCategory.Audio));

        await vm.ScanCommand.ExecuteAsync(null);

        vm.Drivers.Should().HaveCount(2);
        vm.Drivers.Select(d => d.DeviceName).Should().BeEquivalentTo(["A", "B"]);
        vm.IsScanning.Should().BeFalse();
        vm.StatusText.Should().StartWith("Done.");
        vm.ProgressText.Should().Be("2 drivers (0 confirmed, 0 vendor checks)");
    }

    [WpfFact]
    public async Task ScanAsync_removes_rows_missing_from_current_inventory()
    {
        var vm = NewVm(NewDriver("Only", DriverCategory.Display));
        vm.Drivers.Add(new DriverRowViewModel(NewDriver("Known", DriverCategory.Other)));

        await vm.ScanCommand.ExecuteAsync(null);

        vm.Drivers.Should().ContainSingle();
        vm.Drivers.Single().DeviceName.Should().Be("Only");
        vm.ScannedCount.Should().Be(1);
    }

    [WpfFact]
    public async Task Filter_by_search_text_narrows_view()
    {
        var vm = NewVm(
            NewDriver("Intel Wireless AX211", DriverCategory.Network),
            NewDriver("NVIDIA RTX 4070", DriverCategory.Display));

        await vm.ScanCommand.ExecuteAsync(null);

        vm.SearchText = "intel";

        vm.DriversView.Cast<DriverRowViewModel>().Should().ContainSingle(d => d.DeviceName.Contains("Intel"));
    }

    [WpfFact]
    public async Task Filter_by_category_narrows_view()
    {
        var vm = NewVm(
            NewDriver("A", DriverCategory.Display),
            NewDriver("B", DriverCategory.Audio));

        await vm.ScanCommand.ExecuteAsync(null);

        vm.CategoryFilter = DriverCategory.Display;

        vm.DriversView.Cast<DriverRowViewModel>().Should().ContainSingle(d => d.Category == DriverCategory.Display);
    }

    [Fact]
    public void Available_update_filters_have_user_friendly_labels()
    {
        var vm = NewVm();

        vm.AvailableUpdateFilters.Select(f => f.Label).Should().Equal(
            "All drivers",
            "Updates available",
            "No update available");
    }

    private static MainViewModel NewVm(params DriverInfo[] drivers) =>
        new(new FakeScanService(drivers),
            Array.Empty<IUpdateSource>(),
            new NullOemDetectionService(),
            new NullInstallPipeline(),
            new NullInstallConfirmation(),
            new NullHistoryWindowOpener(),
            new NullSettingsWindowOpener(),
            new NullLogsWindowOpener(),
            NullLogger<MainViewModel>.Instance);

    private static DriverInfo NewDriver(string name, DriverCategory category) => new(
        DeviceId: $"ID\\{name}",
        HardwareId: $"HW\\{name}",
        DeviceName: name,
        Category: category,
        Provider: "TestProvider",
        Manufacturer: "TestMaker",
        CurrentVersion: new Version(1, 0, 0, 0),
        CurrentDate: new DateOnly(2024, 1, 1),
        InfName: "oem.inf",
        InfPath: null,
        IsSigned: true,
        DeviceClass: category.ToString());

    private sealed class FakeScanService : IDriverScanService
    {
        private readonly IReadOnlyList<DriverInfo> _drivers;

        public FakeScanService(IReadOnlyList<DriverInfo> drivers)
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
}
