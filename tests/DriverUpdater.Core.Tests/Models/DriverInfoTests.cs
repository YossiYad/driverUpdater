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
        var variants = new[]
        {
            original with { DeviceId = "different" },
            original with { HardwareId = "different" },
            original with { DeviceName = "different" },
            original with { Category = DriverCategory.Audio },
            original with { Provider = "different" },
            original with { Manufacturer = "different" },
            original with { CurrentVersion = new Version(2, 0, 0, 0) },
            original with { CurrentDate = new DateOnly(2025, 1, 1) },
            original with { InfName = "different.inf" },
            original with { InfPath = "C:\\different.inf" },
            original with { IsSigned = false },
            original with { DeviceClass = "different" },
            original with { HardwareIds = new[] { "different" } }
        };

        variants.Should().OnlyContain(variant => variant != original);
    }

    [Theory]
    [InlineData("PCI\\VEN_8086", 1)]
    [InlineData("", 0)]
    [InlineData(" ", 0)]
    public void HardwareIds_contains_only_a_nonblank_primary_hardware_id(string hardwareId, int expectedCount)
    {
        var driver = NewSample(hardwareId);

        driver.HardwareIds.Should().HaveCount(expectedCount);
    }

    private static DriverInfo NewSample(string hardwareId = "PCI\\VEN_8086&DEV_1234&REV_01") => new(
        DeviceId: "PCI\\VEN_8086&DEV_1234",
        HardwareId: hardwareId,
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
