using DriverUpdater.Infrastructure.Catalog;
using FluentAssertions;

namespace DriverUpdater.Infrastructure.Tests.Catalog;

public class CatalogHtmlParserTests
{
    private static readonly string FixtureDir = Path.Combine(AppContext.BaseDirectory, "Catalog", "Fixtures");

    [Fact]
    public void ParseSearchResults_extracts_two_hits_from_fixture()
    {
        var html = File.ReadAllText(Path.Combine(FixtureDir, "search-results.html"));

        var hits = CatalogHtmlParser.ParseSearchResults(html);

        hits.Should().HaveCount(2);

        hits[0].UpdateId.Should().Be("00000000-1111-2222-3333-444444444444");
        hits[0].Title.Should().Be("Intel Corporation - Display - 30.0.101.4502");
        hits[0].Products.Should().Be("Windows 11, Servicing Drivers");
        hits[0].Classification.Should().Be("Drivers");
        hits[0].LastUpdatedDate.Should().Be(new DateOnly(2024, 3, 6));
        hits[0].Version.Should().Be(new Version(30, 0, 101, 4502));
        hits[0].SizeBytes.Should().Be((long)(148.5 * 1024 * 1024));

        hits[1].UpdateId.Should().Be("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        hits[1].Title.Should().Be("Realtek Semiconductor Corp. - Audio - 6.0.9658.1");
        hits[1].LastUpdatedDate.Should().Be(new DateOnly(2024, 1, 15));
        hits[1].Version.Should().Be(new Version(6, 0, 9658, 1));
        hits[1].SizeBytes.Should().Be(120 * 1024);
    }

    [Fact]
    public void ParseSearchResults_returns_empty_when_table_is_missing()
    {
        var html = "<html><body><p>No results.</p></body></html>";

        var hits = CatalogHtmlParser.ParseSearchResults(html);

        hits.Should().BeEmpty();
    }

    [Fact]
    public void ParseDownloadDialog_extracts_two_download_urls_from_fixture()
    {
        var body = File.ReadAllText(Path.Combine(FixtureDir, "download-dialog.html"));

        var downloads = CatalogHtmlParser.ParseDownloadDialog(body);

        downloads.Should().HaveCount(2);
        downloads.Should().Contain(d => d.UpdateId == "00000000-1111-2222-3333-444444444444"
            && d.DownloadUrl.AbsoluteUri == "https://download.windowsupdate.com/c/msdownload/update/driver/drvs/2024/03/intel-display.cab");
        downloads.Should().Contain(d => d.UpdateId == "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"
            && d.DownloadUrl.AbsoluteUri == "https://download.windowsupdate.com/d/msdownload/update/driver/drvs/2024/01/realtek-audio.cab");
    }

    [Fact]
    public void ParseFormTokens_reads_viewstate_and_validation_from_hidden_inputs()
    {
        var html = File.ReadAllText(Path.Combine(FixtureDir, "search-results.html"));

        var tokens = CatalogHtmlParser.ParseFormTokens(html);

        tokens.ViewState.Should().Be("VIEWSTATE_FAKE_TOKEN");
        tokens.EventValidation.Should().Be("EVENT_FAKE_TOKEN");
        tokens.ViewStateGenerator.Should().Be("GEN123");
    }

    [Theory]
    [InlineData("148.5 MB", 155713536L)]
    [InlineData("120 KB", 122880L)]
    [InlineData("1.0 GB", 1073741824L)]
    [InlineData("512 B", 512L)]
    public void ParseSize_handles_known_units(string input, long expected)
    {
        CatalogHtmlParser.ParseSize(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("nonsense")]
    public void ParseSize_returns_null_for_invalid_input(string input)
    {
        CatalogHtmlParser.ParseSize(input).Should().BeNull();
    }
}
