using DriverUpdater.Services.Sources.Internal.Motherboard.Asus;
using FluentAssertions;

namespace DriverUpdater.Services.Tests.Sources.Internal.Motherboard.Asus;

public class AsusMotherboardScraperTests
{
    [Fact]
    public void TryParseResponse_returns_false_for_empty_json()
    {
        AsusMotherboardScraper.TryParseResponse("", out var entries).Should().BeFalse();
        entries.Should().BeEmpty();
    }

    [Fact]
    public void TryParseResponse_returns_false_for_object_without_result()
    {
        AsusMotherboardScraper.TryParseResponse("""{"status":"ok"}""", out var entries).Should().BeFalse();
        entries.Should().BeEmpty();
    }

    [Fact]
    public void TryParseResponse_parses_result_array()
    {
        var json = """
            {
              "Result": [
                {
                  "Title": "Realtek LAN Driver",
                  "Version": "11.29.1025.2023",
                  "Date": "2023/10/25",
                  "DownloadUrl": "https://dlcdnets.asus.com/pub/ASUS/mb/BIOS/lan.zip",
                  "Category": "LAN"
                }
              ]
            }
            """;

        var ok = AsusMotherboardScraper.TryParseResponse(json, out var entries);

        ok.Should().BeTrue();
        entries.Should().HaveCount(1);
        var e = entries[0];
        e.Title.Should().Be("Realtek LAN Driver");
        e.Version.Should().Be("11.29.1025.2023");
        e.ReleaseDate.Should().Be(new DateOnly(2023, 10, 25));
        e.DownloadUrl.AbsoluteUri.Should().Be("https://dlcdnets.asus.com/pub/ASUS/mb/BIOS/lan.zip");
        e.Category.Should().Be("LAN");
    }

    [Fact]
    public void TryParseResponse_parses_direct_array_root()
    {
        var json = """
            [
              {
                "Title": "Intel Wi-Fi Driver",
                "Version": "23.10.0",
                "Date": "2023-09-01",
                "DownloadUrl": "https://dlcdnets.asus.com/pub/ASUS/mb/wifi.zip",
                "Category": "Wireless LAN"
              }
            ]
            """;

        AsusMotherboardScraper.TryParseResponse(json, out var entries).Should().BeTrue();
        entries.Should().HaveCount(1);
        entries[0].Title.Should().Be("Intel Wi-Fi Driver");
    }

    [Fact]
    public void TryParseResponse_skips_entries_without_url()
    {
        var json = """
            {
              "Result": [
                { "Title": "Good Driver", "Version": "1.0", "Date": "2023/01/01", "DownloadUrl": "https://example.com/d.zip", "Category": "LAN" },
                { "Title": "No URL Driver", "Version": "1.0", "Date": "2023/01/01", "Category": "Audio" }
              ]
            }
            """;

        AsusMotherboardScraper.TryParseResponse(json, out var entries).Should().BeTrue();
        entries.Should().HaveCount(1);
        entries[0].Title.Should().Be("Good Driver");
    }

    [Theory]
    [InlineData("2023/10/25", 2023, 10, 25)]
    [InlineData("2023-10-25", 2023, 10, 25)]
    [InlineData("10/25/2023", 2023, 10, 25)]
    [InlineData("2023.10.25", 2023, 10, 25)]
    public void TryParseDate_handles_common_formats(string raw, int year, int month, int day)
    {
        AsusMotherboardScraper.TryParseDate(raw, out var date).Should().BeTrue();
        date.Should().Be(new DateOnly(year, month, day));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-date")]
    public void TryParseDate_returns_false_for_invalid_input(string? raw)
    {
        AsusMotherboardScraper.TryParseDate(raw, out _).Should().BeFalse();
    }
}
