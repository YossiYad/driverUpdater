using System.IO;
using System.Text.RegularExpressions;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using DriverUpdater.Core.Options;
using DriverUpdater.Services.Web;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DriverUpdater.Services.Install;

public sealed partial class VendorPageInstallerResolver : IVendorPageInstallerResolver
{
    public const string HttpClientName = "VendorPageResolver";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<VendorPageInstallerResolver> _logger;
    private readonly Lazy<IBrowserHtmlFetcher>? _browserFetcher;
    private readonly IOptionsMonitor<ScraperSettings>? _scraperSettings;

    public VendorPageInstallerResolver(
        IHttpClientFactory httpClientFactory,
        ILogger<VendorPageInstallerResolver> logger,
        Lazy<IBrowserHtmlFetcher>? browserFetcher = null,
        IOptionsMonitor<ScraperSettings>? scraperSettings = null)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(logger);
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _browserFetcher = browserFetcher;
        _scraperSettings = scraperSettings;
    }

    public async Task<UpdateCandidate?> TryResolveAsync(UpdateCandidate candidate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        if (candidate.InstallKind != UpdateInstallKind.VendorPage)
        {
            return null;
        }

        if (candidate.DownloadUrl.Scheme is not ("http" or "https"))
        {
            _logger.LogInformation(
                "Vendor page resolve skipped for {SourceUpdateId}: {Url} is not an http(s) page",
                candidate.SourceUpdateId, candidate.DownloadUrl);
            return null;
        }

        _logger.LogInformation(
            "Vendor page resolve starting for {SourceUpdateId} ({Device}): fetching {Url} to look for a directly installable driver package",
            candidate.SourceUpdateId, candidate.ForHardwareId, candidate.DownloadUrl);

        string? html = null;
        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, candidate.DownloadUrl);
            request.Headers.Referrer = new Uri(candidate.DownloadUrl.GetLeftPart(UriPartial.Authority));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                _logger.LogWarning(
                    "Vendor page resolve for {SourceUpdateId}: {Url} returned HTTP {Status} ({StatusText}) to the plain HTTP fetch.",
                    candidate.SourceUpdateId, candidate.DownloadUrl, (int)response.StatusCode, response.StatusCode);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Vendor page resolve for {SourceUpdateId}: plain HTTP fetch of {Url} failed.",
                candidate.SourceUpdateId, candidate.DownloadUrl);
        }

        // Anti-bot walls (e.g. Akamai on gigabyte.com) reject HttpClient no matter which
        // headers it sends, because they fingerprint the TLS/HTTP2 layer. A real browser
        // session passes, so when one is enabled we retry the fetch through it.
        if (html is null)
        {
            html = await TryFetchViaBrowserAsync(candidate, cancellationToken).ConfigureAwait(false);
        }

        if (html is null)
        {
            _logger.LogWarning(
                "Vendor page resolve failed for {SourceUpdateId}: could not fetch {Url}. " +
                "The row will fall back to opening the page in a browser.",
                candidate.SourceUpdateId, candidate.DownloadUrl);
            return null;
        }

        if (!TryFindInstallerLink(candidate.DownloadUrl, html, out var packageUrl, out var installerKind))
        {
            LogInstallerCandidateDiagnostics(candidate, html);
            return null;
        }

        _logger.LogInformation(
            "Vendor page {Url} resolved to direct installer {Package} (kind {Kind}) for {SourceUpdateId}",
            candidate.DownloadUrl, packageUrl, installerKind, candidate.SourceUpdateId);
        return candidate with
        {
            DownloadUrl = packageUrl,
            InstallKind = UpdateInstallKind.VendorInstaller,
            SourceUpdateId = $"vendor-installer:{installerKind}:resolved:{candidate.SourceUpdateId}"
        };
    }

    private async Task<string?> TryFetchViaBrowserAsync(UpdateCandidate candidate, CancellationToken cancellationToken)
    {
        if (_browserFetcher is null)
        {
            return null;
        }
        if (_scraperSettings is not null && !_scraperSettings.CurrentValue.EnablePlaywrightFallback)
        {
            _logger.LogInformation(
                "Vendor page resolve for {SourceUpdateId}: browser fallback is disabled (EnablePlaywrightFallback=false), not retrying {Url}.",
                candidate.SourceUpdateId, candidate.DownloadUrl);
            return null;
        }

        _logger.LogInformation(
            "Vendor page resolve for {SourceUpdateId}: retrying {Url} through a real browser session.",
            candidate.SourceUpdateId, candidate.DownloadUrl);
        return await _browserFetcher.Value.TryFetchHtmlAsync(candidate.DownloadUrl, cancellationToken).ConfigureAwait(false);
    }

    // Explains, in the logs, why a vendor page could not be turned into a silent install so it
    // fell back to opening a browser. Lists every downloadable link found on the page and how
    // it was classified - most often the page only offers .exe installers whose unattended
    // flags are not yet known (TryClassifyExe rejects them), which is the signal for what to add.
    private void LogInstallerCandidateDiagnostics(UpdateCandidate candidate, string html)
    {
        var links = new List<string>();
        var rejectedExes = new List<string>();
        foreach (Match match in HrefPattern().Matches(html))
        {
            var raw = match.Groups["url"].Value;
            if (!Uri.TryCreate(candidate.DownloadUrl, raw, out var resolved)
                || resolved.Scheme is not ("http" or "https"))
            {
                continue;
            }

            var extension = Path.GetExtension(resolved.LocalPath);
            if (extension is not (".exe" or ".msi" or ".zip"))
            {
                continue;
            }

            links.Add($"{Path.GetFileName(resolved.LocalPath)} ({extension})");
            if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) && !TryClassifyExe(resolved, out _))
            {
                rejectedExes.Add(resolved.AbsoluteUri);
            }
        }

        _logger.LogWarning(
            "Vendor page resolve for {SourceUpdateId} ({Device}) found no installable package on {Url} " +
            "({Bytes} bytes, {LinkCount} downloadable link(s): {Links}). The row falls back to opening the page. " +
            "To install this in-app, the page needs a recognised .msi/.zip, or one of its .exe links must be added " +
            "to the known unattended-installer list with the correct silent flags.",
            candidate.SourceUpdateId, candidate.ForHardwareId, candidate.DownloadUrl, html.Length,
            links.Count, links.Count == 0 ? "none" : string.Join(", ", links));

        if (rejectedExes.Count > 0)
        {
            _logger.LogInformation(
                "Vendor page resolve for {SourceUpdateId}: {Count} .exe link(s) were rejected as unrecognised installers " +
                "(no known silent flags): {Exes}. Add matching entries to TryClassifyExe/TryBuildVendorInstallerCommand to auto-install these.",
                candidate.SourceUpdateId, rejectedExes.Count, string.Join(", ", rejectedExes));
        }
    }

    internal static bool TryFindInstallerLink(Uri page, string html, out Uri packageUrl, out string installerKind)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(html);

        Uri? msi = null;
        Uri? zip = null;
        Uri? exe = null;
        var exeKind = string.Empty;

        foreach (Match match in HrefPattern().Matches(html))
        {
            var raw = match.Groups["url"].Value;
            if (!Uri.TryCreate(page, raw, out var resolved)
                || resolved.Scheme is not ("http" or "https"))
            {
                continue;
            }

            var extension = Path.GetExtension(resolved.LocalPath);
            if (msi is null && extension.Equals(".msi", StringComparison.OrdinalIgnoreCase))
            {
                msi = resolved;
            }
            else if (zip is null && extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                zip = resolved;
            }
            else if (exe is null
                && extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
                && TryClassifyExe(resolved, out var kind))
            {
                exe = resolved;
                exeKind = kind;
            }
        }

        if (msi is not null)
        {
            packageUrl = msi;
            installerKind = "msi-wrapper";
            return true;
        }
        if (exe is not null)
        {
            packageUrl = exe;
            installerKind = exeKind;
            return true;
        }
        if (zip is not null)
        {
            packageUrl = zip;
            installerKind = "zip-inf";
            return true;
        }

        packageUrl = null!;
        installerKind = string.Empty;
        return false;
    }

    // Only .exe installers with documented unattended flags are auto-resolved; an
    // unrecognised exe would download and then be rejected by the pipeline as
    // "not approved for unattended install" anyway. AMD Adrenalin exes are
    // deliberately excluded - their web stubs always open a GUI.
    internal static bool TryClassifyExe(Uri url, out string installerKind)
    {
        var fileName = Path.GetFileName(url.LocalPath);
        if (url.Host.Contains("nvidia", StringComparison.OrdinalIgnoreCase))
        {
            installerKind = "nvidia";
            return true;
        }
        if (fileName.StartsWith("amd_chipset_software", StringComparison.OrdinalIgnoreCase))
        {
            installerKind = "amd-chipset";
            return true;
        }

        installerKind = string.Empty;
        return false;
    }

    [GeneratedRegex(@"href\s*=\s*[""'](?<url>[^""'#]+\.(?:msi|zip|exe)(?:\?[^""']*)?)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex HrefPattern();
}
