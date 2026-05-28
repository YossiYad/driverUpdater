using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace DriverUpdater.Services.Sources.Internal.Gigabyte;

// Heavy-weight fallback that boots a real headless Chromium via Playwright so the SPA
// can run its JavaScript and bypass Akamai's User-Agent heuristics. First run downloads
// ~250 MB of browser binaries via Playwright's install flow. Guarded behind the
// EnablePlaywrightFallback setting.
public sealed class GigabytePlaywrightScraper : IGigabyteScraper, IAsyncDisposable
{
    internal const int PageLoadTimeoutMs = 30_000;

    private const string StealthScript = """
        Object.defineProperty(navigator, 'webdriver', { get: () => undefined });
        Object.defineProperty(navigator, 'languages', { get: () => ['en-US', 'en'] });
        Object.defineProperty(navigator, 'plugins', { get: () => [
            { name: 'PDF Viewer' },
            { name: 'Chrome PDF Viewer' },
            { name: 'Chromium PDF Viewer' },
            { name: 'Microsoft Edge PDF Viewer' },
            { name: 'WebKit built-in PDF' }
        ] });
        Object.defineProperty(navigator, 'hardwareConcurrency', { get: () => 16 });
        Object.defineProperty(navigator, 'deviceMemory', { get: () => 8 });
        window.chrome = { runtime: {}, loadTimes: function() {}, csi: function() {}, app: {} };
        const originalQuery = window.navigator.permissions ? window.navigator.permissions.query : null;
        if (originalQuery) {
            window.navigator.permissions.query = parameters =>
                parameters.name === 'notifications'
                    ? Promise.resolve({ state: Notification.permission, name: 'notifications' })
                    : originalQuery(parameters);
        }
        const getParameter = WebGLRenderingContext.prototype.getParameter;
        WebGLRenderingContext.prototype.getParameter = function(parameter) {
            if (parameter === 37445) { return 'Intel Inc.'; }
            if (parameter === 37446) { return 'Intel Iris OpenGL Engine'; }
            return getParameter.apply(this, [parameter]);
        };
        """;

    private readonly ILogger<GigabytePlaywrightScraper> _logger;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private bool _chromiumInstalled;

    public GigabytePlaywrightScraper(ILogger<GigabytePlaywrightScraper> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public async Task<IReadOnlyList<GigabyteDriverEntry>> GetDriversAsync(string motherboardModel, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(motherboardModel);
        await EnsureBrowserAsync(cancellationToken).ConfigureAwait(false);

        var normalized = GigabyteApiScraper.NormalizeModel(motherboardModel);
        // The Support tab is React-gated and only renders the driver list when the URL
        // includes the #Support-Driver fragment - omitting it leaves the page on the
        // "Specifications" tab with no a[href*=download.gigabyte.com] anchors.
        var url = $"https://www.gigabyte.com/Motherboard/{Uri.EscapeDataString(normalized)}/support#Support-Driver";
        _logger.LogInformation("GigabytePlaywright: navigating to {Url}", url);

        await using var context = await _browser!.NewContextAsync(new BrowserNewContextOptions
        {
            UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
            ViewportSize = new ViewportSize { Width = 1920, Height = 1080 },
            Locale = "en-US",
            TimezoneId = "Asia/Jerusalem",
            ExtraHTTPHeaders = new Dictionary<string, string>
            {
                ["Accept"] = "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8",
                ["Accept-Language"] = "en-US,en;q=0.9",
                ["Sec-CH-UA"] = "\"Chromium\";v=\"120\", \"Google Chrome\";v=\"120\", \"Not?A_Brand\";v=\"24\"",
                ["Sec-CH-UA-Mobile"] = "?0",
                ["Sec-CH-UA-Platform"] = "\"Windows\""
            }
        }).ConfigureAwait(false);

        // Inject stealth shims BEFORE any page script runs. These erase the
        // navigator.webdriver flag, fake a small plugins/languages set, and define
        // window.chrome - the four signals Akamai's bot manager checks first.
        await context.AddInitScriptAsync(StealthScript).ConfigureAwait(false);

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

            await ClickFirstMatchAsync(page, ["a:has-text(\"Support\")", "button:has-text(\"Support\")", "[data-tab*='Support']", "li:has-text(\"Support\")"], "Support tab");
            await ClickFirstMatchAsync(page, ["a:has-text(\"Driver\")", "button:has-text(\"Driver\")", "li:has-text(\"Driver\")", "[data-id='Support-Driver']"], "Driver subtab");

            await page.WaitForSelectorAsync("a[href*='download.gigabyte.com/FileList/Driver']",
                new PageWaitForSelectorOptions { Timeout = PageLoadTimeoutMs, State = WaitForSelectorState.Attached })
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
        {
            _logger.LogWarning(ex, "GigabytePlaywright: driver list never rendered on {Url} (final URL: {Final}, title: {Title})",
                url, page.Url, await SafeTitleAsync(page).ConfigureAwait(false));
            await SaveDiagnosticsAsync(page, normalized, cancellationToken).ConfigureAwait(false);
            return Array.Empty<GigabyteDriverEntry>();
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

        var parsed = new List<GigabyteDriverEntry>();
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

            parsed.Add(new GigabyteDriverEntry(title.Trim(), version, releaseDate, canonicalUrl, SizeBytes: null, category));
        }

        _logger.LogInformation("GigabytePlaywright: found {Count} driver links on {FinalUrl} (started from {StartUrl})", parsed.Count, page.Url, url);
        if (parsed.Count == 0)
        {
            await SaveDiagnosticsAsync(page, normalized, cancellationToken).ConfigureAwait(false);
        }
        return parsed;
    }

    private async Task ClickFirstMatchAsync(IPage page, string[] selectors, string description)
    {
        foreach (var selector in selectors)
        {
            try
            {
                var locator = page.Locator(selector).First;
                await locator.WaitForAsync(new LocatorWaitForOptions { Timeout = 5_000, State = WaitForSelectorState.Visible }).ConfigureAwait(false);
                await locator.ClickAsync(new LocatorClickOptions { Timeout = 5_000 }).ConfigureAwait(false);
                _logger.LogInformation("GigabytePlaywright: clicked {Description} via selector {Selector}", description, selector);
                await page.WaitForTimeoutAsync(500).ConfigureAwait(false); // let React rebind
                return;
            }
            catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
            {
                _logger.LogDebug("GigabytePlaywright: selector {Selector} for {Description} did not match", selector, description);
            }
        }
        _logger.LogInformation("GigabytePlaywright: no clickable element matched for {Description}; relying on URL fragment", description);
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

    private async Task EnsureBrowserAsync(CancellationToken cancellationToken)
    {
        if (_browser is not null)
        {
            return;
        }

        await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_browser is not null)
            {
                return;
            }

            if (!_chromiumInstalled)
            {
                _logger.LogInformation("Playwright: installing Chromium (~250 MB download on first run)");
                var exit = Microsoft.Playwright.Program.Main(["install", "chromium"]);
                if (exit != 0)
                {
                    throw new ScraperUnavailableException($"Playwright install returned exit code {exit}");
                }
                _chromiumInstalled = true;
            }

            _playwright = await Playwright.CreateAsync().ConfigureAwait(false);

            // First try the user's installed Chrome (or Edge) so the TLS/HTTP2
            // fingerprint matches the browser they use day-to-day. Akamai's bot
            // manager fingerprints far below the JS layer, so the bundled headless
            // Chromium gets caught even with stealth shims. Real Chrome via the
            // chrome channel keeps the realistic fingerprint but still drives via
            // CDP. Falls back to bundled Chromium if neither channel is installed.
            var launchOptions = new BrowserTypeLaunchOptions
            {
                Headless = true,
                Timeout = PageLoadTimeoutMs,
                Args = ["--disable-blink-features=AutomationControlled"]
            };

            foreach (var channel in new[] { "chrome", "msedge", string.Empty })
            {
                try
                {
                    launchOptions.Channel = string.IsNullOrEmpty(channel) ? null : channel;
                    _browser = await _playwright.Chromium.LaunchAsync(launchOptions).ConfigureAwait(false);
                    _logger.LogInformation("Playwright: launched browser channel={Channel}", string.IsNullOrEmpty(channel) ? "chromium-bundled" : channel);
                    break;
                }
                catch (Exception ex) when (ex is PlaywrightException or InvalidOperationException)
                {
                    _logger.LogInformation("Playwright: channel {Channel} not available ({Reason}); trying next", string.IsNullOrEmpty(channel) ? "chromium-bundled" : channel, ex.Message);
                }
            }

            if (_browser is null)
            {
                throw new ScraperUnavailableException("No Chromium-based browser channel could be launched");
            }
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null)
        {
            await _browser.CloseAsync().ConfigureAwait(false);
        }
        _playwright?.Dispose();
        _initLock.Dispose();
    }

    private static string? ExtractVersion(string text)
    {
        var match = System.Text.RegularExpressions.Regex.Match(text, @"\b\d+(?:\.\d+){1,3}\b");
        return match.Success ? match.Value : null;
    }

    private static DateOnly? ExtractDate(string text)
    {
        var match = System.Text.RegularExpressions.Regex.Match(text, @"\b(\d{4}[-/.]\d{1,2}[-/.]\d{1,2})\b");
        if (match.Success && DateOnly.TryParse(match.Groups[1].Value.Replace('.', '-').Replace('/', '-'), CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return date;
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
