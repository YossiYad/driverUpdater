using DriverUpdater.Services.Sources.Internal.Motherboard;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace DriverUpdater.Services.Web;

/// <summary>
/// Owns the single Playwright browser instance shared by every component that needs a real
/// browser (Gigabyte scraping, vendor-page fetching). Boots the user's installed Chrome or
/// Edge first so the TLS/HTTP2 fingerprint matches a real browser - Akamai-style bot managers
/// fingerprint far below the JS layer, so bundled headless Chromium gets caught even with
/// stealth shims. Chromium is downloaded only if neither Chrome nor Edge is available.
/// </summary>
public sealed class PlaywrightBrowserProvider : IAsyncDisposable
{
    internal const int LaunchTimeoutMs = 30_000;

    internal const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

    // Erases the navigator.webdriver flag, fakes a small plugins/languages set, and defines
    // window.chrome - the signals bot managers check first, injected before any page script runs.
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

    private readonly ILogger<PlaywrightBrowserProvider> _logger;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private bool _chromiumInstalled;

    public PlaywrightBrowserProvider(ILogger<PlaywrightBrowserProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <summary>Creates a browser context with the stealth shims and Chrome-like headers applied.</summary>
    public async Task<IBrowserContext> NewStealthContextAsync(CancellationToken cancellationToken = default)
    {
        await EnsureBrowserAsync(cancellationToken).ConfigureAwait(false);

        var context = await _browser!.NewContextAsync(new BrowserNewContextOptions
        {
            UserAgent = UserAgent,
            ViewportSize = new ViewportSize { Width = 1920, Height = 1080 },
            Locale = "en-US",
            ExtraHTTPHeaders = new Dictionary<string, string>
            {
                ["Accept"] = "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8",
                ["Accept-Language"] = "en-US,en;q=0.9",
                ["Sec-CH-UA"] = "\"Chromium\";v=\"120\", \"Google Chrome\";v=\"120\", \"Not?A_Brand\";v=\"24\"",
                ["Sec-CH-UA-Mobile"] = "?0",
                ["Sec-CH-UA-Platform"] = "\"Windows\""
            }
        }).ConfigureAwait(false);

        await context.AddInitScriptAsync(StealthScript).ConfigureAwait(false);
        return context;
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

            _playwright = await Playwright.CreateAsync().ConfigureAwait(false);

            var launchOptions = new BrowserTypeLaunchOptions
            {
                Headless = true,
                Timeout = LaunchTimeoutMs,
                Args = ["--disable-blink-features=AutomationControlled"]
            };

            foreach (var channel in new[] { "chrome", "msedge" })
            {
                try
                {
                    launchOptions.Channel = channel;
                    _browser = await _playwright.Chromium.LaunchAsync(launchOptions).ConfigureAwait(false);
                    _logger.LogInformation("Playwright: launched browser channel={Channel}", channel);
                    break;
                }
                catch (Exception ex) when (ex is PlaywrightException or InvalidOperationException)
                {
                    _logger.LogInformation("Playwright: channel {Channel} not available ({Reason}); trying next", channel, ex.Message);
                }
            }

            if (_browser is null)
            {
                if (!_chromiumInstalled)
                {
                    _logger.LogInformation("Playwright: Chrome and Edge unavailable; installing Chromium (~250 MB)");
                    var exit = Microsoft.Playwright.Program.Main(["install", "chromium"]);
                    if (exit != 0)
                    {
                        throw new ScraperUnavailableException($"Playwright install returned exit code {exit}");
                    }
                    _chromiumInstalled = true;
                }

                launchOptions.Channel = null;
                try
                {
                    _browser = await _playwright.Chromium.LaunchAsync(launchOptions).ConfigureAwait(false);
                    _logger.LogInformation("Playwright: launched bundled Chromium");
                }
                catch (Exception ex) when (ex is PlaywrightException or InvalidOperationException)
                {
                    _logger.LogInformation("Playwright: bundled Chromium unavailable ({Reason})", ex.Message);
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
}
