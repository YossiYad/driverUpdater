using DriverUpdater.Services.Sources.Internal.Motherboard;
using DriverUpdater.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DriverUpdater.Services.Sources.Internal.Motherboard.Gigabyte;

// Routes scrape requests: API first (cheap), then Playwright if the API blows up and
// the user has opted into the heavy browser fallback. Any failure beyond that point
// degrades to an empty result so OfficialVendorPageSource can keep producing the
// advisory it always has.
public sealed class HybridGigabyteScraper : IMotherboardScraper
{
    private readonly IMotherboardScraper _api;
    private readonly Lazy<IMotherboardScraper> _playwright;
    private readonly IOptionsMonitor<ScraperSettings> _settings;
    private readonly ILogger<HybridGigabyteScraper> _logger;

    public HybridGigabyteScraper(
        IMotherboardScraper api,
        Lazy<IMotherboardScraper> playwright,
        IOptionsMonitor<ScraperSettings> settings,
        ILogger<HybridGigabyteScraper> logger)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(playwright);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);
        _api = api;
        _playwright = playwright;
        _settings = settings;
        _logger = logger;
    }

    public async Task<IReadOnlyList<MotherboardDriverEntry>> GetDriversAsync(string motherboardModel, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _api.GetDriversAsync(motherboardModel, cancellationToken).ConfigureAwait(false);
        }
        catch (ScraperUnavailableException apiEx)
        {
            _logger.LogWarning(apiEx, "Gigabyte API unavailable");
        }

        if (!_settings.CurrentValue.EnablePlaywrightFallback)
        {
            _logger.LogInformation("Playwright fallback is disabled in settings; no Gigabyte drivers will be returned");
            return Array.Empty<MotherboardDriverEntry>();
        }

        try
        {
            _logger.LogInformation("Falling back to Playwright headless browser for Gigabyte support page");
            return await _playwright.Value.GetDriversAsync(motherboardModel, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception playwrightEx)
        {
            _logger.LogWarning(playwrightEx, "Playwright fallback also failed; returning no Gigabyte drivers");
            return Array.Empty<MotherboardDriverEntry>();
        }
    }
}
