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
        // Gigabyte's current support page renders the driver tables directly when this
        // fragment is used. Older selectors targeted a retired React tab and left the
        // scraper waiting on a page that already contained the catalog.
        var url = $"https://www.gigabyte.com/Motherboard/{Uri.EscapeDataString(normalized)}/support#support-dl-driver";
        _logger.LogInformation("GigabytePlaywright: navigating to {Url}", url);

        await using var context = await _browserProvider.NewStealthContextAsync(cancellationToken).ConfigureAwait(false);

        var page = await context.NewPageAsync().ConfigureAwait(false);
        try
        {
            await page.GotoAsync(url, new PageGotoOptions { Timeout = PageLoadTimeoutMs, WaitUntil = WaitUntilState.DOMContentLoaded }).ConfigureAwait(false);
            if (TryBuildCanonicalSupportUrl(page.Url, out var canonicalSupportUrl))
            {
                _logger.LogInformation(
                    "GigabytePlaywright: product redirect removed the support path; navigating to canonical support page {Url}",
                    canonicalSupportUrl);
                await page.GotoAsync(
                    canonicalSupportUrl,
                    new PageGotoOptions { Timeout = PageLoadTimeoutMs, WaitUntil = WaitUntilState.DOMContentLoaded })
                    .ConfigureAwait(false);
            }
        }
        catch (PlaywrightException ex)
        {
            throw new ScraperUnavailableException("Playwright navigation failed", ex);
        }

        try
        {
            await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded,
                new PageWaitForLoadStateOptions { Timeout = PageLoadTimeoutMs }).ConfigureAwait(false);
            await page.WaitForSelectorAsync("tr.item-group a[href*='download.gigabyte.com/FileList/Driver']",
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

        // Read the named cells from the current server-rendered table. Keeping the category
        // heading is important because MotherboardSource uses it to bind each package to the
        // correct installed device instead of treating the page as one generic vendor lead.
        var links = await page.EvalOnSelectorAllAsync<DriverScrape[]>(
            "tr.item-group a[href*='download.gigabyte.com/FileList/Driver']",
            "elements => elements.map(e => { " +
            "  const row = e.closest('tr.item-group'); " +
            "  const table = row ? row.closest('table') : null; " +
            "  const heading = table ? table.previousElementSibling : null; " +
            "  return { " +
            "    Href: e.href, " +
            "    Title: row?.querySelector('.item-info')?.textContent?.trim() || '', " +
            "    Version: row?.querySelector('.item-version')?.textContent?.trim() || '', " +
            "    Date: row?.querySelector('.item-date')?.textContent?.trim() || '', " +
            "    Size: row?.querySelector('.item-size')?.textContent?.trim() || '', " +
            "    Category: heading?.tagName === 'H2' ? heading.textContent?.trim() || '' : '' " +
            "  };" +
            "})"
        ).ConfigureAwait(false);

        var parsed = new List<MotherboardDriverEntry>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var link in links)
        {
            if (TryBuildEntry(
                    link.Href,
                    link.Title,
                    link.Version,
                    link.Date,
                    link.Size,
                    link.Category,
                    out var entry)
                && seenUrls.Add(entry.DownloadUrl.AbsoluteUri))
            {
                parsed.Add(entry);
            }
        }

        _logger.LogInformation("GigabytePlaywright: found {Count} driver links on {FinalUrl} (started from {StartUrl})", parsed.Count, page.Url, url);
        if (parsed.Count == 0)
        {
            await SaveDiagnosticsAsync(page, normalized, cancellationToken).ConfigureAwait(false);
        }
        return parsed;
    }

    internal static bool TryBuildCanonicalSupportUrl(string currentUrl, out string supportUrl)
    {
        supportUrl = string.Empty;
        if (!Uri.TryCreate(currentUrl, UriKind.Absolute, out var current)
            || current.Scheme is not ("http" or "https")
            || !current.Host.EndsWith("gigabyte.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var path = current.AbsolutePath.TrimEnd('/');
        if (!path.StartsWith("/Motherboard/", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/support", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        supportUrl = new UriBuilder(current.Scheme, current.Host)
        {
            Path = path + "/support",
            Fragment = "support-dl-driver"
        }.Uri.AbsoluteUri;
        return true;
    }

    internal static bool TryBuildEntry(
        string href,
        string title,
        string versionText,
        string dateText,
        string sizeText,
        string categoryText,
        out MotherboardDriverEntry entry)
    {
        if (!Uri.TryCreate(href, UriKind.Absolute, out var downloadUrl)
            || downloadUrl.Scheme is not ("http" or "https"))
        {
            entry = null!;
            return false;
        }

        var canonicalUrl = new Uri(downloadUrl.GetLeftPart(UriPartial.Path));
        var fileName = Path.GetFileName(canonicalUrl.AbsolutePath);
        var version = ExtractVersion(versionText)
            ?? ExtractVersionFromFileName(fileName)
            ?? "0.0";
        var releaseDate = ExtractDate(dateText) ?? DateOnly.MinValue;
        var normalizedTitle = string.IsNullOrWhiteSpace(title) ? "Gigabyte Driver" : title.Trim();
        var category = string.IsNullOrWhiteSpace(categoryText)
            ? GuessCategory(normalizedTitle)
            : categoryText.Trim();
        entry = new MotherboardDriverEntry(
            normalizedTitle,
            version,
            releaseDate,
            canonicalUrl,
            ParseSizeBytes(sizeText),
            category);
        return true;
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

    private static long? ParseSizeBytes(string text)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            text,
            @"(?<size>\d+(?:\.\d+)?)\s*(?<unit>KB|MB|GB)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success
            || !double.TryParse(match.Groups["size"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var size))
        {
            return null;
        }

        var multiplier = match.Groups["unit"].Value.ToUpperInvariant() switch
        {
            "KB" => 1024d,
            "MB" => 1024d * 1024d,
            "GB" => 1024d * 1024d * 1024d,
            _ => 1d
        };
        return checked((long)(size * multiplier));
    }

    private sealed class DriverScrape
    {
        public string Href { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string Date { get; set; } = string.Empty;
        public string Size { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
    }
}
