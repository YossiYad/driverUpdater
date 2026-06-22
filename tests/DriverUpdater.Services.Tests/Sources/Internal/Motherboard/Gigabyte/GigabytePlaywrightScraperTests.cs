using DriverUpdater.Services.Sources.Internal.Motherboard;
using DriverUpdater.Services.Sources.Internal.Motherboard.Gigabyte;
using FluentAssertions;
using System.Runtime.InteropServices;

namespace DriverUpdater.Services.Tests.Sources.Internal.Motherboard.Gigabyte;

public class GigabytePlaywrightScraperTests
{
    [Theory]
    [InlineData(Architecture.X64, true)]
    [InlineData(Architecture.X86, false)]
    [InlineData(Architecture.Arm64, false)]
    public void SupportsArchitecture_only_accepts_x64(Architecture architecture, bool expected)
    {
        GigabytePlaywrightScraper.SupportsArchitecture(architecture).Should().Be(expected);
    }

    [Theory]
    [InlineData("mb_driver_612_realtekdch_6.0.9927.1.zip", "6.0.9927.1")]
    [InlineData("mb_driver_amdchipset_8.03.25.247.zip", "8.03.25.247")]
    [InlineData("mb_driver_apu_25.10.42.0.exe", "25.10.42.0")]
    [InlineData("mb_driver_short_1.2.3.zip", "1.2.3")]
    public void ExtractVersionFromFileName_pulls_version_segment(string fileName, string expected)
    {
        GigabytePlaywrightScraper.ExtractVersionFromFileName(fileName).Should().Be(expected);
    }

    [Theory]
    [InlineData("readme.zip")]
    [InlineData("mb_driver_no_version.zip")]
    [InlineData("random_name_12.png")]
    public void ExtractVersionFromFileName_returns_null_when_no_version_segment(string fileName)
    {
        GigabytePlaywrightScraper.ExtractVersionFromFileName(fileName).Should().BeNull();
    }
}
