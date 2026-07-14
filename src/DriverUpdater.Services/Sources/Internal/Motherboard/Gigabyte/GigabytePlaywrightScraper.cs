using DriverUpdater.Services.Sources.Internal.Motherboard;
using DriverUpdater.Services.Web;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace DriverUpdater.Services.Sources.Internal.Motherboard.Gigabyte;

// Heavy-weight fallback that boots a real headless Chromium via Playwright so the SPA
// can run its JavaScript and bypass Akamai's User-Agent heuristics. First run downloads
// ~250 MB of browser binaries via Playwright's install flow. Guarded behind the
// EnablePlaywrightFallback setting.
public sealed class GigabytePlaywrightScraper : IMotherboardScraper
{
    internal const int PageLoadTimeoutMs = 30_000;

    private readonly PlaywrightBrowserProvider _browserProvider;
    private readonly ILogger<GigabytePlaywrightScraper> _logger;

    public GigabytePlaywrightScraper(PlaywrightBrowserProvider browserProvider, ILogger<GigabytePlaywrightScraper> logger)
    {
        ArgumentNullException.ThrowIfNull(browserProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _browserProvider = browserProvider;
        _logger = logger;
    }

    public async Task<IReadOnlyList<MotherboardDriverEntry>> GetDriversAsync(string motherboardModel, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(motherboardModel);

        var normalized = GigabyteApiScraper.NormalizeModel(motherboardModel);
        // The Support tab is React-gated and only renders the driver list when the URL
        // includes the #Support-Driver fragment - omitting it leaves the page on the
        // "Specifications" tab with no a[href*=download.gigabyte.com] anchors.
        var url = $"https://www.gigabyte.com/Motherboard/{Uri.EscapeDataString(normalized)}/support#Support-Driver";
        _logger.LogInformation("GigabytePlaywright: navigating to {Url}", url);

        await using var context = await _browserProvider.NewStealthContextAsync(cancellationToken).ConfigureAwait(false);

        var page = await context.NewPageAsync().ConfigureAwait(false);
        try
        {
            await page.GotoAsync(url, new PageGotoOptions { Timeout = PageLoadTimeoutMs, WaitUntil = WaitUntilState.DOMContentLoaded }).ConfigureAwait(false);
        }
        catch (PlaywrightException ex)
        {
            throw new ScraperUnavailableException("Playwright navigation failed", ex);
        }

        // The /support URL redirects to /Motherboard/{slug}#Support-Driver but the React
        // tab controller does not pick up the hash automatically - the page lands on the
        // Key Features tab with the driver list unmounted. Force the Support tab + Driver
        // subtab via clicks before we wait for the download anchors.
        try
        {
            await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded,
                new PageWaitForLoadStateOptions { Timeout = PageLoadTimeoutMs }).ConfigureAwait(false);

            // Dismiss the CookieYes banner that overlays the tab strip and swallows clicks
            // on the Support tab. The banner only ships an Accept button; with it gone the
            // Element UI tabs become hit-testable.
            await ClickFirstMatchAsync(page,
                ["button.cky-btn-accept", ".cky-btn-accept", "button:has-text(\"Accept\")"],
                "cookie banner Accept",
                quick: true);

            // Gigabyte's product page uses custom .men-tab-item links inside
            // .base-info-tabs for the top-level tabs (Key Features / Specifications /
            // Support / News & Awards / ...). Diagnostic HTML captured live confirmed
            // the link text shows up as the trailing text of .men-tab-item-link.
            // Clicking the Support tab is enough on its own - the #Support-Driver hash
            // in the URL already preselects the Driver sub-section under Support, so a
            // second click does nothing but burn the per-selector wait budget.
            await ClickFirstMatchAsync(page,
                [".men-tab-item-link:has-text(\"Support\")", ".men-tab-item:has-text(\"Support\")"],
                "Support tab");

            await page.WaitForSelectorAsync("a[href*='download.gigabyte.com/FileList/Driver']",
                new PageWaitForSelectorOptions { Timeout = PageLoadTimeoutMs, State = WaitForSelectorState.Attached })
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
        {
            _logger.LogWarning(ex, "GigabytePlaywright: driver list never rendered on {Url} (final URL: {Final}, title: {Title})",
                url, page.Url, await SafeTitleAsync(page).ConfigureAwait(false));
            await SaveDiagnosticsAsync(page, normalized, cancellationToken).ConfigureAwait(false);
            return Array.Empty<MotherboardDriverEntry>();
        }

        // Gigabyte support pages render every driver download as an <a> whose href points
        // at download.gigabyte.com/FileList/Driver/<filename>.zip. The filename embeds the
        // version (mb_driver_612_realtekdch_6.0.9927.1.zip), so we can pull a reliable
        // version even if the surrounding row layout drifts. The row's innerText still
        // gives us the human title ("Realtek HD Audio Driver") and the release date.
        var links = await page.EvalOnSelectorAllAsync<DriverScrape[]>(
            "a[href*='download.gigabyte.com/FileList/Driver']",
            "elements => elements.map(e => { " +
            "  const row = e.closest('tr, li, div.support-list, div.driver-item, div[class*=\"item\"], div[class*=\"row\"]') || e.parentElement; " +
            "  const text = row ? row.innerText : e.innerText; " +
            "  return { Href: e.href, Title: e.getAttribute('title') || (e.innerText||'').trim(), RowText: text };" +
            "})"
        ).ConfigureAwait(false);

        var parsed = new List<MotherboardDriverEntry>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var link in links)
        {
            if (!Uri.TryCreate(link.Href, UriKind.Absolute, out var downloadUrl) || downloadUrl.Scheme is not ("http" or "https"))
            {
                continue;
            }

            // Strip the `?v=...` cache buster so the SourceUpdateId stays stable across
            // page reloads.
            var canonicalUrl = new Uri(downloadUrl.GetLeftPart(UriPartial.Path));
            if (!seenUrls.Add(canonicalUrl.AbsoluteUri))
            {
                continue;
            }

            var rowText = link.RowText ?? string.Empty;
            var fileName = Path.GetFileName(canonicalUrl.AbsolutePath);
            var version = ExtractVersionFromFileName(fileName) ?? ExtractVersion(rowText) ?? "0.0";
            var releaseDate = ExtractDate(rowText) ?? DateOnly.MinValue;
            var title = string.IsNullOrWhiteSpace(link.Title) ? GuessTitle(rowText) : link.Title;
            var category = GuessCategory(title);

            parsed.Add(new MotherboardDriverEntry(title.Trim(), version, releaseDate, canonicalUrl, SizeBytes: null, category));
        }

        _logger.LogInformation("GigabytePlaywright: found {Count} driver links on {FinalUrl} (started from {StartUrl})", parsed.Count, page.Url, url);
        if (parsed.Count == 0)
        {
            await SaveDiagnosticsAsync(page, normalized, cancellationToken).ConfigureAwait(false);
        }
        return parsed;
    }

    private async Task ClickFirstMatchAsync(IPage page, string[] selectors, string description, bool quick = false)
    {
        // `quick` shrinks each selector's wait so optional UI (e.g. the cookie banner)
        // does not eat 20+ seconds when it is not present.
        var perSelectorTimeoutMs = quick ? 1_500 : 4_000;
        foreach (var selector in selectors)
        {
            try
            {
                var locator = page.Locator(selector).First;
                await locator.WaitForAsync(new LocatorWaitForOptions { Timeout = perSelectorTimeoutMs, State = WaitForSelectorState.Visible }).ConfigureAwait(false);
                await locator.ClickAsync(new LocatorClickOptions { Timeout = perSelectorTimeoutMs }).ConfigureAwait(false);
                _logger.LogInformation("GigabytePlaywright: clicked {Description} via selector {Selector}", description, selector);
                await page.WaitForTimeoutAsync(500).ConfigureAwait(false); // let React rebind
                return;
            }
            catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
            {
                _logger.LogDebug("GigabytePlaywright: selector {Selector} for {Description} did not match", selector, description);
            }
        }
        _logger.LogInformation("GigabytePlaywright: no clickable element matched for {Description}", description);
    }

    private static async Task<string> SafeTitleAsync(IPage page)
    {
        try { return await page.TitleAsync().ConfigureAwait(false); }
        catch { return "<unavailable>"; }
    }

    private async Task SaveDiagnosticsAsync(IPage page, string normalizedModel, CancellationToken cancellationToken)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "DriverUpdater",
                "Diagnostics");
            Directory.CreateDirectory(dir);

            var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
            var screenshotPath = Path.Combine(dir, $"gigabyte-{normalizedModel}-{stamp}.png");
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = screenshotPath, FullPage = true }).ConfigureAwait(false);

            var htmlPath = Path.Combine(dir, $"gigabyte-{normalizedModel}-{stamp}.html");
            var html = await page.ContentAsync().ConfigureAwait(false);
            await File.WriteAllTextAsync(htmlPath, html, cancellationToken).ConfigureAwait(false);

            _logger.LogWarning("GigabytePlaywright: saved diagnostics to {Screenshot} and {Html}", screenshotPath, htmlPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GigabytePlaywright: failed to save diagnostics");
        }
    }

    internal static string? ExtractVersionFromFileName(string fileName)
    {
        // mb_driver_612_realtekdch_6.0.9927.1.zip -> 6.0.9927.1
        var match = System.Text.RegularExpressions.Regex.Match(
            fileName,
            @"_(?<version>\d+(?:\.\d+){2,3})\.(?:zip|exe)$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["version"].Value : null;
    }

    private static string? ExtractVersion(string text)
    {
        var match = System.Text.RegularExpressions.Regex.Match(text, @"\b\d+(?:\.\d+){1,3}\b");
        return match.Success ? match.Value : null;
    }

    private static DateOnly? ExtractDate(string text)
    {
        // ISO-ish (2026-05-18, 2026/05/18, 2026.05.18).
        var iso = System.Text.RegularExpressions.Regex.Match(text, @"\b(\d{4}[-/.]\d{1,2}[-/.]\d{1,2})\b");
        if (iso.Success && DateOnly.TryParse(iso.Groups[1].Value.Replace('.', '-').Replace('/', '-'), CultureInfo.InvariantCulture, DateTimeStyles.None, out var isoDate))
        {
            return isoDate;
        }

        // Gigabyte renders dates as "Jan 15, 2026" or "May 24, 2026" in the row text.
        var month = System.Text.RegularExpressions.Regex.Match(
            text,
            @"\b(?<m>Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)[a-z]*\s+(?<d>\d{1,2}),\s*(?<y>\d{4})\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (month.Success
            && DateOnly.TryParseExact(
                $"{month.Groups["m"].Value} {month.Groups["d"].Value}, {month.Groups["y"].Value}",
                ["MMM d, yyyy", "MMM dd, yyyy"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var named))
        {
            return named;
        }

        return null;
    }

    private static string GuessTitle(string rowText)
    {
        var lines = rowText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.FirstOrDefault(l => l.Length > 5 && l.Length < 120) ?? "Gigabyte Driver";
    }

    private static string GuessCategory(string title)
    {
        var lower = title.ToLowerInvariant();
        if (lower.Contains("audio") || lower.Contains("realtek hd")) { return "Audio"; }
        if (lower.Contains("lan") || lower.Contains("ethernet") || lower.Contains("gbe")) { return "LAN"; }
        if (lower.Contains("wireless") || lower.Contains("wifi") || lower.Contains("wi-fi")) { return "Wireless"; }
        if (lower.Contains("bluetooth")) { return "Bluetooth"; }
        if (lower.Contains("chipset")) { return "Chipset"; }
        if (lower.Contains("usb")) { return "USB"; }
        if (lower.Contains("vga") || lower.Contains("graphics")) { return "Graphics"; }
        return "Utility";
    }

    private sealed class DriverScrape
    {
        public string Href { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string RowText { get; set; } = string.Empty;
    }
}
