using DriverUpdater.Core.Models;
using FluentAssertions;

namespace DriverUpdater.Core.Tests.Models;

public class DriverInfoTests
{
    [Fact]
    public void Empty_returns_record_with_given_device_id_and_default_fields()
    {
        var empty = DriverInfo.Empty("ROOT\\TEST\\0001");

        empty.DeviceId.Should().Be("ROOT\\TEST\\0001");
        empty.HardwareId.Should().BeEmpty();
        empty.DeviceName.Should().BeEmpty();
        empty.Category.Should().Be(DriverCategory.Other);
        empty.Provider.Should().BeEmpty();
        empty.Manufacturer.Should().BeEmpty();
        empty.CurrentVersion.Should().BeNull();
        empty.CurrentDate.Should().BeNull();
        empty.InfName.Should().BeNull();
        empty.InfPath.Should().BeNull();
        empty.IsSigned.Should().BeFalse();
        empty.DeviceClass.Should().BeEmpty();
    }

    [Fact]
    public void Two_records_with_identical_fields_are_equal()
    {
        var a = NewSample();
        var b = NewSample();

        a.Should().Be(b);
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void Changing_a_single_field_produces_a_different_record()
    {
        var original = NewSample();
        var modified = original with { CurrentVersion = new Version(2, 0, 0, 0) };

        modified.Should().NotBe(original);
        modified.CurrentVersion.Should().Be(new Version(2, 0, 0, 0));
    }

    private static DriverInfo NewSample() => new(
        DeviceId: "PCI\\VEN_8086&DEV_1234",
        HardwareId: "PCI\\VEN_8086&DEV_1234&REV_01",
        DeviceName: "Sample Display Adapter",
        Category: DriverCategory.Display,
        Provider: "Intel",
        Manufacturer: "Intel Corporation",
        CurrentVersion: new Version(1, 0, 0, 0),
        CurrentDate: new DateOnly(2024, 1, 1),
        InfName: "oem1.inf",
        InfPath: "C:\\Windows\\INF\\oem1.inf",
        IsSigned: true,
        DeviceClass: "Display");
}
