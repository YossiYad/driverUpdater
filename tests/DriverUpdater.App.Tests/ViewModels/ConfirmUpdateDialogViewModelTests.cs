using DriverUpdater.App.ViewModels;
using DriverUpdater.Core.Models;
using FluentAssertions;

namespace DriverUpdater.App.Tests.ViewModels;

public class ConfirmUpdateDialogViewModelTests
{
    [Fact]
    public void Always_required_reboot_shows_required_warning_only()
    {
        var vm = NewViewModel(
            DriverCategory.Network,
            UpdateRebootBehavior.AlwaysRequired);

        vm.ShowRestartRequiredWarning.Should().BeTrue();
        vm.ShowRestartPossibleWarning.Should().BeFalse();
        vm.ShowInstallImpactWarning.Should().BeTrue();
    }

    [Fact]
    public void Possible_or_unknown_reboot_shows_save_work_warning()
    {
        var possible = NewViewModel(
            DriverCategory.Other,
            UpdateRebootBehavior.MayBeRequired);
        var unknown = NewViewModel(
            DriverCategory.Other,
            UpdateRebootBehavior.Unknown);

        possible.ShowRestartPossibleWarning.Should().BeTrue();
        unknown.ShowRestartPossibleWarning.Should().BeTrue();
    }

    [Fact]
    public void Never_required_reboot_does_not_show_restart_warning()
    {
        var vm = NewViewModel(
            DriverCategory.Audio,
            UpdateRebootBehavior.NeverRequired);

        vm.ShowRestartRequiredWarning.Should().BeFalse();
        vm.ShowRestartPossibleWarning.Should().BeFalse();
    }

    [Fact]
    public void Driver_category_selects_the_relevant_interruption_warning()
    {
        var display = NewViewModel(
            DriverCategory.Display,
            UpdateRebootBehavior.NeverRequired);
        var network = NewViewModel(
            DriverCategory.Network,
            UpdateRebootBehavior.NeverRequired);
        var input = NewViewModel(
            DriverCategory.Bluetooth,
            UpdateRebootBehavior.NeverRequired);

        display.ShowDisplayImpact.Should().BeTrue();
        network.ShowNetworkImpact.Should().BeTrue();
        input.ShowInputDeviceImpact.Should().BeTrue();
    }

    [Fact]
    public void Vendor_installer_warns_that_related_apps_may_close()
    {
        var vm = NewViewModel(
            DriverCategory.Other,
            UpdateRebootBehavior.NeverRequired,
            UpdateInstallKind.VendorInstaller);

        vm.ShowVendorInstallerImpact.Should().BeTrue();
        vm.ShowInstallImpactWarning.Should().BeTrue();
    }

    private static ConfirmUpdateDialogViewModel NewViewModel(
        DriverCategory category,
        UpdateRebootBehavior rebootBehavior,
        UpdateInstallKind installKind = UpdateInstallKind.WindowsUpdate)
    {
        var driver = new DriverInfo(
            DeviceId: "DEVICE\\1",
            HardwareId: "HARDWARE\\1",
            DeviceName: "Test device",
            Category: category,
            Provider: "Vendor",
            Manufacturer: "Vendor",
            CurrentVersion: new Version(1, 0, 0, 0),
            CurrentDate: new DateOnly(2025, 1, 1),
            InfName: "oem1.inf",
            InfPath: null,
            IsSigned: true,
            DeviceClass: category.ToString());
        var candidate = new UpdateCandidate(
            ForHardwareId: driver.HardwareId,
            Source: UpdateSource.WindowsUpdate,
            NewVersion: new Version(2, 0, 0, 0),
            NewDate: new DateOnly(2026, 1, 1),
            DownloadUrl: new Uri("https://example.com/driver.cab"),
            SizeBytes: 1024,
            KbArticle: null,
            IsSuperseded: false,
            SourceUpdateId: "update-1",
            SupersededIds: Array.Empty<string>(),
            InstallKind: installKind,
            RebootBehavior: rebootBehavior);
        var operation = UpdateOperation.NewPending(candidate, driver);

        return new ConfirmUpdateDialogViewModel(operation);
    }
}
