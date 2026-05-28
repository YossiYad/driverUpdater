using System.Net;
using DriverUpdater.Services.Sources.Internal.Motherboard;
using DriverUpdater.Services.Sources.Internal.Motherboard.Gigabyte;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriverUpdater.Services.Tests.Sources.Internal.Motherboard.Gigabyte;

public class GigabyteApiScraperTests
{
    private const string SampleResponse = """
        {
          "data": [
            {
              "title": "Realtek HD Audio Driver",
              "version": "6.0.9789.1",
              "date": "2026-03-15",
              "downloadUrl": "https://download.gigabyte.com/Drivers/audio_realtek_6.0.9789.1.zip",
              "size": "362 MB",
              "category": "Audio"
            },
            {
              "title": "Realtek LAN Driver",
              "version": "11.024.0827.2024",
              "date": "2024-08-27",
              "downloadUrl": "https://download.gigabyte.com/Drivers/lan_realtek_11.024.zip",
              "size": "12.4 MB",
              "category": "LAN"
            }
          ]
        }
        """;

    [Fact]
    public async Task GetDriversAsync_parses_driver_list_from_api_response()
    {
        var scraper = NewScraper(new StaticJsonHandler(SampleResponse));

        var entries = await scraper.GetDriversAsync("B850M-GAMING-X-WIFI6E-rev-1x");

        entries.Should().HaveCount(2);
        entries[0].Title.Should().Be("Realtek HD Audio Driver");
        entries[0].Version.Should().Be("6.0.9789.1");
        entries[0].ReleaseDate.Should().Be(new DateOnly(2026, 3, 15));
        entries[0].DownloadUrl.AbsoluteUri.Should().EndWith("audio_realtek_6.0.9789.1.zip");
        entries[0].Category.Should().Be("Audio");
        entries[1].Category.Should().Be("LAN");
    }

    [Fact]
    public async Task GetDriversAsync_throws_ScraperUnavailable_for_403()
    {
        var scraper = NewScraper(new StatusCodeHandler(HttpStatusCode.Forbidden));

        Func<Task> act = () => scraper.GetDriversAsync("B850M");

        await act.Should().ThrowAsync<ScraperUnavailableException>();
    }

    [Fact]
    public async Task GetDriversAsync_throws_ScraperUnavailable_for_unparseable_body()
    {
        var scraper = NewScraper(new StaticJsonHandler("<html>Akamai access denied</html>"));

        Func<Task> act = () => scraper.GetDriversAsync("B850M");

        await act.Should().ThrowAsync<ScraperUnavailableException>();
    }

    [Theory]
    [InlineData("B850M GAMING X WIFI6E", "B850M-GAMING-X-WIFI6E")]
    [InlineData("B850M GAMING X WIFI6E (rev. 1.0)", "B850M-GAMING-X-WIFI6E-rev-10")]
    [InlineData("B850M GAMING X WIFI6E (rev. 1.2)", "B850M-GAMING-X-WIFI6E-rev-12")]
    [InlineData("X670E AORUS MASTER", "X670E-AORUS-MASTER")]
    [InlineData("Z790 AORUS ELITE AX (rev. 1.1)", "Z790-AORUS-ELITE-AX-rev-11")]
    public void NormalizeModel_collapses_spaces_and_handles_rev_suffix(string raw, string expected)
    {
        GigabyteApiScraper.NormalizeModel(raw).Should().Be(expected);
    }

    [Theory]
    [InlineData("2026-03-15", 2026, 3, 15)]
    [InlineData("2024/08/27", 2024, 8, 27)]
    [InlineData("2026.05.01", 2026, 5, 1)]
    public void TryParseDate_handles_common_formats(string raw, int year, int month, int day)
    {
        var ok = GigabyteApiScraper.TryParseDate(raw, out var date);

        ok.Should().BeTrue();
        date.Should().Be(new DateOnly(year, month, day));
    }

    private static GigabyteApiScraper NewScraper(HttpMessageHandler handler)
    {
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://www.gigabyte.com/") };
        return new GigabyteApiScraper(client, NullLogger<GigabyteApiScraper>.Instance);
    }

    private sealed class StaticJsonHandler : HttpMessageHandler
    {
        private readonly string _body;
        public StaticJsonHandler(string body) { _body = body; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_body) });
    }

    private sealed class StatusCodeHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        public StatusCodeHandler(HttpStatusCode status) { _status = status; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(_status) { Content = new StringContent("") });
    }
}
