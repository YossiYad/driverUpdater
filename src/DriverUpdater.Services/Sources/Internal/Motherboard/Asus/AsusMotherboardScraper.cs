using Microsoft.Extensions.Logging;

namespace DriverUpdater.Services.Sources.Internal.Motherboard.Asus;

// Stub implementation. ASUS support pages
// (asus.com/motherboards-components/motherboards/.../helpdesk_download/)
// are JavaScript-rendered SPAs behind Akamai-like protection - the
// same class of problem we solved for Gigabyte in PR #20.
//
// To fill this in, somebody with an ASUS motherboard running the app
// needs to capture a DevTools session and report back:
//   1. The exact support-page URL pattern (slug normalization for spaces,
//      revisions, BIOS variants).
//   2. The cookie consent banner selector to click before any tab is
//      interactive (ASUS uses a custom OneTrust integration).
//   3. The selector that switches the page to the Driver list (typically
//      a tab labelled "Driver & Tools" or "Driver & Utility").
//   4. The download anchor pattern - past captures suggest
//      dlcdnets.asus.com/.../<board>/<chunked>.zip but it changes per
//      product family.
//   5. The release date format used in the row text (ASUS historically
//      shipped yyyy/MM/dd).
//
// Mirror the GigabyteApiScraper / GigabytePlaywrightScraper /
// HybridGigabyteScraper trio under
// Sources/Internal/Motherboard/Asus/, swap this stub out in DI.
public sealed class AsusMotherboardScraper : IMotherboardScraper
{
    private readonly ILogger<AsusMotherboardScraper> _logger;

    public AsusMotherboardScraper(ILogger<AsusMotherboardScraper> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public Task<IReadOnlyList<MotherboardDriverEntry>> GetDriversAsync(string motherboardModel, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "ASUS motherboard scraper is not yet implemented; no candidates will be returned for {Model}. "
          + "Tracking issue: replace this stub with a real scraper at "
          + "src/DriverUpdater.Services/Sources/Internal/Motherboard/Asus/.",
            motherboardModel);
        return Task.FromResult<IReadOnlyList<MotherboardDriverEntry>>(Array.Empty<MotherboardDriverEntry>());
    }
}
