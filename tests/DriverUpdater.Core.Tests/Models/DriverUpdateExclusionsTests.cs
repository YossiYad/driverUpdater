using DriverUpdater.Core.Models;
using FluentAssertions;

namespace DriverUpdater.Core.Tests.Models;

public class DriverUpdateExclusionsTests
{
    [Theory]
    [InlineData("pci\\ven_8086", true)]
    [InlineData("PCI\\VEN_10EC", false)]
    [InlineData("", false)]
    [InlineData(" ", false)]
    [InlineData(null, false)]
    public void Contains_matches_device_ids_case_insensitively_and_rejects_blank_values(
        string? deviceId,
        bool expected)
    {
        var exclusions = new DriverUpdateExclusions(new[] { "PCI\\VEN_8086" });

        exclusions.Contains(deviceId).Should().Be(expected);
    }

    [Fact]
    public void Equality_and_hash_code_use_the_same_case_insensitive_comparison()
    {
        var upper = new DriverUpdateExclusions(new[] { "PCI\\VEN_8086", "USB\\VID_1234" });
        var lower = new DriverUpdateExclusions(new[] { "pci\\ven_8086", "usb\\vid_1234" });

        upper.Should().Be(lower);
        upper.GetHashCode().Should().Be(lower.GetHashCode());
    }

    [Fact]
    public void Equality_still_detects_a_different_device_list()
    {
        var expected = new DriverUpdateExclusions(new[] { "PCI\\VEN_8086" });
        var different = new DriverUpdateExclusions(new[] { "PCI\\VEN_10EC" });

        expected.Should().NotBe(different);
    }
}
