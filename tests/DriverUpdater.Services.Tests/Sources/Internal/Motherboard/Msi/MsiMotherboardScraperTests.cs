using DriverUpdater.Services.Sources.Internal.Motherboard.Msi;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriverUpdater.Services.Tests.Sources.Internal.Motherboard.Msi;

public class MsiMotherboardScraperTests
{
    [Fact]
    public async Task GetDriversAsync_returns_empty_stub_for_any_model()
    {
        var scraper = new MsiMotherboardScraper(NullLogger<MsiMotherboardScraper>.Instance);

        var result = await scraper.GetDriversAsync("MAG B650 TOMAHAWK WIFI");

        result.Should().BeEmpty();
    }
}
