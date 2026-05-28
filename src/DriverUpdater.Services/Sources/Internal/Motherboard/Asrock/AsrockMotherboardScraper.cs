using Microsoft.Extensions.Logging;

namespace DriverUpdater.Services.Sources.Internal.Motherboard.Asrock;

// Stub implementation. ASRock's support pages
// (asrock.com/mb/<platform>/<model>/index.asp#Download) are guarded by
// Incapsula and use a classic ASP-rendered DOM rather than a SPA - so
// the scraping strategy will probably look different from Gigabyte /
// ASUS / MSI.
//
// To fill this in, somebody with an ASRock motherboard running the app
// needs to capture a DevTools session and report back:
//   1. The exact support-page URL pattern - ASRock uses
//      asrock.com/mb/<chipset-vendor>/<board>/index.asp and reveals the
//      download table via a hash fragment (#Download).
//   2. Whether Incapsula serves a JavaScript challenge before the page
//      is reachable (it usually does on first hit).
//   3. The driver download table layout - rows are typically <tr>
//      containing the title, OS, version, date and an inline <a> link.
//   4. The download URL pattern - past captures point at
//      asrock.com/mb/utility/<file>.zip.
//   5. Date format (ASRock historically uses MM/dd/yyyy).
//
// Mirror the GigabyteApiScraper / GigabytePlaywrightScraper /
// HybridGigabyteScraper trio under
// Sources/Internal/Motherboard/Asrock/, swap this stub out in DI.
public sealed class AsrockMotherboardScraper : IMotherboardScraper
{
    private readonly ILogger<AsrockMotherboardScraper> _logger;

    public AsrockMotherboardScraper(ILogger<AsrockMotherboardScraper> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public Task<IReadOnlyList<MotherboardDriverEntry>> GetDriversAsync(string motherboardModel, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "ASRock motherboard scraper is not yet implemented; no candidates will be returned for {Model}. "
          + "Tracking issue: replace this stub with a real scraper at "
          + "src/DriverUpdater.Services/Sources/Internal/Motherboard/Asrock/.",
            motherboardModel);
        return Task.FromResult<IReadOnlyList<MotherboardDriverEntry>>(Array.Empty<MotherboardDriverEntry>());
    }
}
