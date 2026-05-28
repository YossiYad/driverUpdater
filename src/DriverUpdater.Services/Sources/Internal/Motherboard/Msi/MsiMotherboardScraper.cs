using Microsoft.Extensions.Logging;

namespace DriverUpdater.Services.Sources.Internal.Motherboard.Msi;

// Stub implementation. MSI's support pages
// (msi.com/Motherboard/<model>/support) returned HTTP 403 to anything
// other than a real browser session, same Akamai/Cloudflare pattern as
// Gigabyte.
//
// To fill this in, somebody with an MSI motherboard running the app
// needs to capture a DevTools session and report back:
//   1. The exact support-page URL pattern - MSI uses the marketing
//      model name with hyphens (e.g. "MAG-B650-TOMAHAWK-WIFI").
//   2. The cookie consent banner selector (MSI uses CookieYes, same
//      vendor as Gigabyte, so the .cky-btn-accept selector may already
//      work).
//   3. The tab structure - MSI nests Drivers under Support behind a
//      .driver_tab element.
//   4. The download anchor pattern - past captures pointed at
//      download.msi.com/dvr_exe/mb/<chunked>.zip.
//   5. The release date format (MSI usually serves yyyy-MM-dd).
//
// Mirror the GigabyteApiScraper / GigabytePlaywrightScraper /
// HybridGigabyteScraper trio under
// Sources/Internal/Motherboard/Msi/, swap this stub out in DI.
public sealed class MsiMotherboardScraper : IMotherboardScraper
{
    private readonly ILogger<MsiMotherboardScraper> _logger;

    public MsiMotherboardScraper(ILogger<MsiMotherboardScraper> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public Task<IReadOnlyList<MotherboardDriverEntry>> GetDriversAsync(string motherboardModel, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "MSI motherboard scraper is not yet implemented; no candidates will be returned for {Model}. "
          + "Tracking issue: replace this stub with a real scraper at "
          + "src/DriverUpdater.Services/Sources/Internal/Motherboard/Msi/.",
            motherboardModel);
        return Task.FromResult<IReadOnlyList<MotherboardDriverEntry>>(Array.Empty<MotherboardDriverEntry>());
    }
}
