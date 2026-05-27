using System.Runtime.CompilerServices;
using DriverUpdater.App.Tests.Stubs;
using DriverUpdater.App.ViewModels;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriverUpdater.App.Tests.ViewModels;

public class MainViewModelOemTests
{
    [WpfFact]
    public async Task InitializeAsync_sets_detected_oem_when_service_returns_info()
    {
        var oem = new OemInfo(
            Vendor: OemVendor.Lenovo,
            Manufacturer: "LENOVO",
            Model: "ThinkPad X1",
            ToolName: "Lenovo Vantage",
            ToolPath: null,
            FallbackUrl: new Uri("https://support.lenovo.com"));

        var vm = NewVm(oem);

        await vm.InitializeAsync();

        vm.DetectedOem.Should().Be(oem);
        vm.HasOem.Should().BeTrue();
    }

    [WpfFact]
    public async Task InitializeAsync_leaves_detected_oem_null_when_service_returns_null()
    {
        var vm = NewVm(null);

        await vm.InitializeAsync();

        vm.DetectedOem.Should().BeNull();
        vm.HasOem.Should().BeFalse();
    }

    [WpfFact]
    public async Task InitializeAsync_swallows_detection_errors()
    {
        var vm = new MainViewModel(
            new EmptyScanService(),
            Array.Empty<IUpdateSource>(),
            new ThrowingOemService(),
            new NullInstallPipeline(),
            new NullInstallConfirmation(),
            NullLogger<MainViewModel>.Instance);

        Func<Task> act = () => vm.InitializeAsync();

        await act.Should().NotThrowAsync();
        vm.DetectedOem.Should().BeNull();
    }

    [WpfFact]
    public void OpenOemTool_command_is_disabled_when_no_oem()
    {
        var vm = NewVm(null);

        vm.OpenOemToolCommand.CanExecute(null).Should().BeFalse();
    }

    private static MainViewModel NewVm(OemInfo? oem) =>
        new(new EmptyScanService(),
            Array.Empty<IUpdateSource>(),
            new ConstantOemService(oem),
            new NullInstallPipeline(),
            new NullInstallConfirmation(),
            NullLogger<MainViewModel>.Instance);

    private sealed class ConstantOemService : IOemDetectionService
    {
        private readonly OemInfo? _info;
        public ConstantOemService(OemInfo? info) { _info = info; }
        public Task<OemInfo?> DetectAsync(CancellationToken cancellationToken = default) => Task.FromResult(_info);
    }

    private sealed class ThrowingOemService : IOemDetectionService
    {
        public Task<OemInfo?> DetectAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("simulated failure");
    }

    private sealed class EmptyScanService : IDriverScanService
    {
#pragma warning disable CS1998
        public async IAsyncEnumerable<DriverInfo> ScanAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield break;
        }
#pragma warning restore CS1998
    }
}
