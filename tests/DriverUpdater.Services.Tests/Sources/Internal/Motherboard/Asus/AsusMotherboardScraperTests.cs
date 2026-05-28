using DriverUpdater.Services.Sources.Internal.Motherboard.Asus;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriverUpdater.Services.Tests.Sources.Internal.Motherboard.Asus;

public class AsusMotherboardScraperTests
{
    [Fact]
    public async Task GetDriversAsync_returns_empty_stub_for_any_model()
    {
        var scraper = new AsusMotherboardScraper(NullLogger<AsusMotherboardScraper>.Instance);

        var result = await scraper.GetDriversAsync("ROG STRIX X670E-E GAMING WIFI");

        result.Should().BeEmpty();
    }
}
