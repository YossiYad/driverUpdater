using DriverUpdater.Services.Sources.Internal.Motherboard;
using DriverUpdater.Services.Sources.Internal.Motherboard.Gigabyte;
using FluentAssertions;

namespace DriverUpdater.Services.Tests.Sources.Internal.Motherboard.Gigabyte;

public class GigabytePlaywrightScraperTests
{
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

    [Fact]
    public void TryBuildEntry_maps_the_current_support_table_for_any_board_model()
    {
        var ok = GigabytePlaywrightScraper.TryBuildEntry(
            "https://download.gigabyte.com/FileList/Driver/mb_driver_612_realtekdch_6.0.9927.1.zip?v=cache",
            "Realtek HD Audio Driver",
            "6.0.9927.1",
            "Jul 22, 2026",
            "35.5 MB",
            "Audio",
            out var entry);

        ok.Should().BeTrue();
        entry.Title.Should().Be("Realtek HD Audio Driver");
        entry.Version.Should().Be("6.0.9927.1");
        entry.ReleaseDate.Should().Be(new DateOnly(2026, 7, 22));
        entry.DownloadUrl.Should().Be(
            new Uri("https://download.gigabyte.com/FileList/Driver/mb_driver_612_realtekdch_6.0.9927.1.zip"));
        entry.SizeBytes.Should().Be(37_224_448);
        entry.Category.Should().Be("Audio");
    }

    [Fact]
    public void TryBuildCanonicalSupportUrl_repairs_the_product_redirect()
    {
        var ok = GigabytePlaywrightScraper.TryBuildCanonicalSupportUrl(
            "https://www.gigabyte.com/Motherboard/B850M-GAMING-X-WIFI6E-rev-10#support-dl-driver",
            out var supportUrl);

        ok.Should().BeTrue();
        supportUrl.Should().Be(
            "https://www.gigabyte.com/Motherboard/B850M-GAMING-X-WIFI6E-rev-10/support#support-dl-driver");
    }

    [Fact]
    public void TryBuildCanonicalSupportUrl_does_not_loop_on_an_existing_support_page()
    {
        var ok = GigabytePlaywrightScraper.TryBuildCanonicalSupportUrl(
            "https://www.gigabyte.com/Motherboard/B850M-GAMING-X-WIFI6E-rev-10/support#support-dl-driver",
            out _);

        ok.Should().BeFalse();
    }

}
