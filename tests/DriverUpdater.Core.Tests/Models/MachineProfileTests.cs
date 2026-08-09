using DriverUpdater.Core.Models;
using FluentAssertions;

namespace DriverUpdater.Core.Tests.Models;

public class MachineProfileTests
{
    [Fact]
    public void Describe_lists_every_detail_that_was_read()
    {
        var profile = Full();

        var description = profile.Describe();

        description.Should().Contain("- System: ASUSTeK COMPUTER INC. Vivobook X1502VA (family: Vivobook, SKU: X1502VA, type: x64-based PC)");
        description.Should().Contain("- Motherboard: ASUSTeK COMPUTER INC. X1502VA (revision: 1.0)");
        description.Should().Contain("- BIOS: American Megatrends X1502VA.308 (released: 2026-01-14)");
        description.Should().Contain("- CPU: 13th Gen Intel(R) Core(TM) i5-13500H (12 cores, 16 threads)");
        description.Should().Contain("- GPU: Intel(R) Iris(R) Xe Graphics");
        description.Should().Contain("- Memory: 16 GB");
        description.Should().Contain("- Windows: Microsoft Windows 11 Home (version: 10.0.26200, build: 26200, 64-bit)");
    }

    [Fact]
    public void Describe_omits_lines_whose_values_are_missing()
    {
        var profile = MachineProfile.Empty with
        {
            SystemManufacturer = "Dell Inc.",
            SystemModel = "XPS 15 9530"
        };

        var description = profile.Describe();

        description.Should().Be("- System: Dell Inc. XPS 15 9530");
    }

    [Theory]
    [InlineData("To Be Filled By O.E.M.")]
    [InlineData("System Product Name")]
    [InlineData("Default string")]
    [InlineData("None")]
    [InlineData("   ")]
    public void Describe_drops_the_placeholders_OEMs_leave_in_the_firmware(string placeholder)
    {
        // Feeding "To Be Filled By O.E.M." into a search query is worse than saying nothing.
        var profile = MachineProfile.Empty with
        {
            SystemManufacturer = "Acme",
            SystemModel = "Real Model",
            BaseBoardManufacturer = placeholder,
            BaseBoardProduct = placeholder
        };

        profile.Describe().Should().Be("- System: Acme Real Model");
    }

    [Fact]
    public void Describe_does_not_repeat_a_manufacturer_the_model_already_starts_with()
    {
        var profile = MachineProfile.Empty with
        {
            SystemManufacturer = "ASUS",
            SystemModel = "ASUS TUF Gaming F15"
        };

        profile.Describe().Should().Be("- System: ASUS TUF Gaming F15");
    }

    [Fact]
    public void HasAnyDetail_is_false_for_a_profile_that_read_nothing()
    {
        MachineProfile.Empty.HasAnyDetail.Should().BeFalse();
        MachineProfile.Empty.Describe().Should().BeEmpty();
    }

    [Fact]
    public void HasAnyDetail_is_false_when_every_value_is_a_placeholder()
    {
        var profile = MachineProfile.Empty with
        {
            SystemManufacturer = "System manufacturer",
            SystemModel = "System Product Name"
        };

        profile.HasAnyDetail.Should().BeFalse();
    }

    private static MachineProfile Full() => new(
        SystemManufacturer: "ASUSTeK COMPUTER INC.",
        SystemModel: "Vivobook X1502VA",
        SystemFamily: "Vivobook",
        SystemSku: "X1502VA",
        BaseBoardManufacturer: "ASUSTeK COMPUTER INC.",
        BaseBoardProduct: "X1502VA",
        BaseBoardVersion: "1.0",
        BiosManufacturer: "American Megatrends",
        BiosVersion: "X1502VA.308",
        BiosReleaseDate: new DateOnly(2026, 1, 14),
        ProcessorName: "13th Gen Intel(R) Core(TM) i5-13500H",
        ProcessorCores: 12,
        ProcessorLogicalProcessors: 16,
        GraphicsAdapters: new[] { "Intel(R) Iris(R) Xe Graphics" },
        TotalPhysicalMemoryBytes: 17_179_869_184,
        OperatingSystemName: "Microsoft Windows 11 Home",
        OperatingSystemVersion: "10.0.26200",
        OperatingSystemBuild: "26200",
        OperatingSystemArchitecture: "64-bit",
        SystemType: "x64-based PC");
}
