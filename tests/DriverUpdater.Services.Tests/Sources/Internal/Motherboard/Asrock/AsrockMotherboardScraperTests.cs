using DriverUpdater.Services.Sources.Internal.Motherboard.Asrock;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriverUpdater.Services.Tests.Sources.Internal.Motherboard.Asrock;

public class AsrockMotherboardScraperTests
{
    [Fact]
    public async Task GetDriversAsync_returns_empty_stub_for_any_model()
    {
        var scraper = new AsrockMotherboardScraper(NullLogger<AsrockMotherboardScraper>.Instance);

        var result = await scraper.GetDriversAsync("X670E Taichi");

        result.Should().BeEmpty();
    }
}
