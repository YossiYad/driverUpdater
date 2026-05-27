using DriverUpdater.App.ViewModels;
using DriverUpdater.Core.Models;
using FluentAssertions;

namespace DriverUpdater.App.Tests.ViewModels;

public class DriverRowViewModelTests
{
    [Fact]
    public void Constructor_throws_when_driver_is_null()
    {
        var act = () => new DriverRowViewModel(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Computed_properties_forward_to_driver()
    {
        var driver = NewSampleDriver();

        var row = new DriverRowViewModel(driver);

        row.DeviceName.Should().Be(driver.DeviceName);
        row.Provider.Should().Be(driver.Provider);
        row.Manufacturer.Should().Be(driver.Manufacturer);
        row.Category.Should().Be(driver.Category);
        row.DeviceClass.Should().Be(driver.DeviceClass);
        row.HardwareId.Should().Be(driver.HardwareId);
        row.CurrentVersionText.Should().Be("1.2.3.4");
        row.CurrentDateText.Should().Be("2024-03-06");
        row.IsSigned.Should().Be(driver.IsSigned);
    }

    [Fact]
    public void Current_version_text_is_null_when_driver_has_no_version()
    {
        var driver = DriverInfo.Empty("ROOT\\X");

        var row = new DriverRowViewModel(driver);

        row.CurrentVersionText.Should().BeNull();
        row.CurrentDateText.Should().BeNull();
    }

    [Fact]
    public void Status_defaults_to_unknown_and_can_be_changed()
    {
        var row = new DriverRowViewModel(NewSampleDriver());

        row.Status.Should().Be(DriverStatus.Unknown);

        row.Status = DriverStatus.Outdated;

        row.Status.Should().Be(DriverStatus.Outdated);
    }

    [Fact]
    public void Available_update_setter_exposes_version_date_and_source()
    {
        var row = new DriverRowViewModel(NewSampleDriver());

        row.AvailableVersionText.Should().BeNull();
        row.AvailableDateText.Should().BeNull();
        row.SourceText.Should().BeNull();

        row.AvailableUpdate = new UpdateCandidate(
            ForHardwareId: row.HardwareId,
            Source: UpdateSource.MicrosoftCatalog,
            NewVersion: new Version(2, 0, 0, 0),
            NewDate: new DateOnly(2026, 1, 1),
            DownloadUrl: new Uri("https://example.com/x.cab"),
            SizeBytes: 1024,
            KbArticle: null,
            IsSuperseded: false,
            SourceUpdateId: "abc",
            SupersededIds: Array.Empty<string>());

        row.AvailableVersionText.Should().Be("2.0.0.0");
        row.AvailableDateText.Should().Be("2026-01-01");
        row.SourceText.Should().Be("MicrosoftCatalog");
    }

    private static DriverInfo NewSampleDriver() => new(
        DeviceId: "PCI\\VEN_8086&DEV_1234\\3&1&0",
        HardwareId: "PCI\\VEN_8086&DEV_1234",
        DeviceName: "Sample Adapter",
        Category: DriverCategory.Network,
        Provider: "Intel",
        Manufacturer: "Intel Corporation",
        CurrentVersion: new Version(1, 2, 3, 4),
        CurrentDate: new DateOnly(2024, 3, 6),
        InfName: "oem1.inf",
        InfPath: "C:\\Windows\\INF\\oem1.inf",
        IsSigned: true,
        DeviceClass: "Net");
}
