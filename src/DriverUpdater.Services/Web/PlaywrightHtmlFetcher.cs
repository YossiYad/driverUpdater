using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace DriverUpdater.Services.Web;

/// <summary>
/// Fetches a page's rendered HTML through a real browser. Used as a fallback when a plain
/// HTTP fetch of a vendor page is blocked by anti-bot measures (403) despite browser-like
/// headers - the browser's genuine TLS/JS fingerprint passes where HttpClient cannot.
/// </summary>
public interface IBrowserHtmlFetcher
{
    Task<string?> TryFetchHtmlAsync(Uri url, CancellationToken cancellationToken = default);
}

public sealed class PlaywrightHtmlFetcher : IBrowserHtmlFetcher
{
    private const int PageLoadTimeoutMs = 30_000;

    private readonly PlaywrightBrowserProvider _browserProvider;
    private readonly ILogger<PlaywrightHtmlFetcher> _logger;

    public PlaywrightHtmlFetcher(PlaywrightBrowserProvider browserProvider, ILogger<PlaywrightHtmlFetcher> logger)
    {
        ArgumentNullException.ThrowIfNull(browserProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _browserProvider = browserProvider;
        _logger = logger;
    }

    public async Task<string?> TryFetchHtmlAsync(Uri url, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);

        try
        {
            await using var context = await _browserProvider.NewStealthContextAsync(cancellationToken).ConfigureAwait(false);
            var page = await context.NewPageAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            var response = await page.GotoAsync(url.AbsoluteUri, new PageGotoOptions
            {
                Timeout = PageLoadTimeoutMs,
                WaitUntil = WaitUntilState.DOMContentLoaded
            })
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);

            if (response is not null && response.Status >= 400)
            {
                _logger.LogWarning(
                    "Browser fetch of {Url} still returned HTTP {Status}; the site blocks even a real browser session",
                    url, response.Status);
                return null;
            }

            // Give SPA pages a moment to render their download links after DOMContentLoaded.
            await Task.Delay(1_500, cancellationToken).ConfigureAwait(false);
            var html = await page.ContentAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Browser fetch of {Url} returned {Length} chars of rendered HTML", url, html.Length);
            return html;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Browser fetch of {Url} failed", url);
            return null;
        }
    }
}
