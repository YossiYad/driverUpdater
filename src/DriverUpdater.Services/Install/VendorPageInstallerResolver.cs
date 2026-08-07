using System.IO;
using System.Text.Json;
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

    public async Task<VendorPageResolution> TryResolveAsync(UpdateCandidate candidate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        if (candidate.InstallKind != UpdateInstallKind.VendorPage)
        {
            return VendorPageResolution.NoPackageFound;
        }

        if (candidate.DownloadUrl.Scheme is not ("http" or "https"))
        {
            _logger.LogInformation(
                "Vendor page resolve skipped for {SourceUpdateId}: {Url} is not an http(s) page",
                candidate.SourceUpdateId, candidate.DownloadUrl);
            return VendorPageResolution.NoPackageFound;
        }

        _logger.LogInformation(
            "Vendor page resolve starting for {SourceUpdateId} ({Device}): fetching {Url} to look for a directly installable driver package",
            candidate.SourceUpdateId, candidate.ForHardwareId, candidate.DownloadUrl);

        if (await TryResolveGitHubReleaseAsync(candidate, cancellationToken).ConfigureAwait(false) is { } githubResolution)
        {
            return githubResolution;
        }

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
        var browserAlreadyTried = false;
        if (html is null)
        {
            browserAlreadyTried = true;
            html = await TryFetchViaBrowserAsync(candidate, cancellationToken).ConfigureAwait(false);
        }

        if (html is null)
        {
            _logger.LogWarning(
                "Vendor page resolve failed for {SourceUpdateId}: could not fetch {Url} at all, even through a " +
                "browser session. This usually means the lead itself is bad (a moved or invented URL) rather than " +
                "a page the app just can't parse, so the update will not be offered.",
                candidate.SourceUpdateId, candidate.DownloadUrl);
            return VendorPageResolution.PageUnreachable;
        }

        // A vendor download page lists packages for the whole product family - amd.com offers
        // the Adrenalin graphics installer next to the chipset one. Filtering while scanning
        // (instead of only rejecting the winner afterwards) keeps looking past a package that
        // belongs to a different vendor family than the device this candidate is for.
        bool IsCompatible(Uri url, string kind) => IsPackageCompatibleWithHardware(candidate, url, kind);

        Uri packageUrl;
        string installerKind;
        if (!TryFindInstallerLink(candidate.DownloadUrl, html, out packageUrl, out installerKind, IsCompatible))
        {
            // A page that answers 200 with no package links is usually a download list that
            // builds its links in the browser (realtek.com and amd.com both do this). The
            // browser retry used to run only when the plain fetch failed outright, so those
            // pages were written off as having nothing to install without ever being rendered.
            var rendered = browserAlreadyTried
                ? null
                : await TryFetchViaBrowserAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (rendered is null
                || !TryFindInstallerLink(candidate.DownloadUrl, rendered, out packageUrl, out installerKind, IsCompatible))
            {
                LogInstallerCandidateDiagnostics(candidate, rendered ?? html);
                return VendorPageResolution.NoPackageFound;
            }

            _logger.LogInformation(
                "Vendor page {Url} only exposed its packages after rendering in a browser",
                candidate.DownloadUrl);
        }

        if (!IsPackageCompatibleWithHardware(candidate, packageUrl, installerKind))
        {
            _logger.LogWarning(
                "Vendor page package {Package} was rejected for {HardwareId}: its vendor family does not match the device",
                packageUrl,
                candidate.ForHardwareId);
            return VendorPageResolution.NoPackageFound;
        }

        _logger.LogInformation(
            "Vendor page {Url} resolved to direct installer {Package} (kind {Kind}) for {SourceUpdateId}",
            candidate.DownloadUrl, packageUrl, installerKind, candidate.SourceUpdateId);
        return VendorPageResolution.Installer(candidate with
        {
            DownloadUrl = packageUrl,
            InstallKind = UpdateInstallKind.VendorInstaller,
            Confidence = ResolvedConfidence(candidate),
            SourceUpdateId = $"vendor-installer:{installerKind}:resolved:{candidate.SourceUpdateId}"
        });
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

    private async Task<VendorPageResolution?> TryResolveGitHubReleaseAsync(
        UpdateCandidate candidate,
        CancellationToken cancellationToken)
    {
        if (!TryBuildGitHubReleaseApiUri(candidate.DownloadUrl, out var apiUri))
        {
            return null;
        }

        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, apiUri);
            request.Headers.Accept.Clear();
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "GitHub release API returned HTTP {Status} for {Url}; falling back to the release page",
                    (int)response.StatusCode,
                    candidate.DownloadUrl);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!document.RootElement.TryGetProperty("assets", out var assets)
                || assets.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var assetLinks = assets.EnumerateArray()
                .Select(asset => (
                    Name: asset.TryGetProperty("name", out var name) ? name.GetString() : null,
                    Url: asset.TryGetProperty("browser_download_url", out var url) ? url.GetString() : null))
                .Where(asset => IsInstallableGitHubAsset(asset.Name, asset.Url))
                .OrderByDescending(asset => GitHubAssetScore(asset.Name!))
                .Select(asset => $"<a href=\"{asset.Url}\">{asset.Name}</a>")
                .ToArray();
            if (assetLinks.Length == 0)
            {
                _logger.LogInformation("GitHub release {Url} contains no Windows installer assets", candidate.DownloadUrl);
                return null;
            }

            bool IsCompatible(Uri url, string kind) => IsPackageCompatibleWithHardware(candidate, url, kind);
            if (!TryFindInstallerLink(
                    candidate.DownloadUrl,
                    string.Join('\n', assetLinks),
                    out var packageUrl,
                    out var installerKind,
                    IsCompatible))
            {
                return null;
            }

            _logger.LogInformation(
                "GitHub release {Url} resolved through the official API to {Package} (kind {Kind})",
                candidate.DownloadUrl,
                packageUrl,
                installerKind);
            return VendorPageResolution.Installer(candidate with
            {
                DownloadUrl = packageUrl,
                InstallKind = UpdateInstallKind.VendorInstaller,
                Confidence = ResolvedConfidence(candidate),
                SourceUpdateId = $"vendor-installer:{installerKind}:github:{candidate.SourceUpdateId}"
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GitHub release API lookup failed for {Url}; falling back to the release page", candidate.DownloadUrl);
            return null;
        }
    }

    internal static bool TryBuildGitHubReleaseApiUri(Uri page, out Uri apiUri)
    {
        ArgumentNullException.ThrowIfNull(page);
        apiUri = null!;
        if (!page.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var segments = page.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 3
            || !segments[2].Equals("releases", StringComparison.OrdinalIgnoreCase)
            || !GitHubNamePattern().IsMatch(segments[0])
            || !GitHubNamePattern().IsMatch(segments[1]))
        {
            return false;
        }

        var releasePath = "latest";
        if (segments.Length >= 5 && segments[3].Equals("tag", StringComparison.OrdinalIgnoreCase))
        {
            var tag = string.Join('/', segments.Skip(4));
            releasePath = $"tags/{Uri.EscapeDataString(tag)}";
        }

        apiUri = new Uri($"https://api.github.com/repos/{segments[0]}/{segments[1]}/releases/{releasePath}");
        return true;
    }

    private static bool IsInstallableGitHubAsset(string? name, string? url)
    {
        if (string.IsNullOrWhiteSpace(name)
            || string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        var extension = Path.GetExtension(name).ToLowerInvariant();
        if (extension is not (".exe" or ".msi" or ".zip"))
        {
            return false;
        }

        return !name.Contains("source", StringComparison.OrdinalIgnoreCase)
            && !name.Contains("symbol", StringComparison.OrdinalIgnoreCase)
            && !name.Contains("debug", StringComparison.OrdinalIgnoreCase)
            && !name.Contains("pdb", StringComparison.OrdinalIgnoreCase);
    }

    private static int GitHubAssetScore(string name)
    {
        var score = Path.GetExtension(name).ToLowerInvariant() switch
        {
            ".msi" => 30,
            ".exe" => 20,
            ".zip" => 10,
            _ => 0
        };
        if (name.Contains("x64", StringComparison.OrdinalIgnoreCase)
            || name.Contains("amd64", StringComparison.OrdinalIgnoreCase)
            || name.Contains("win64", StringComparison.OrdinalIgnoreCase))
        {
            score += 5;
        }
        if (name.Contains("arm64", StringComparison.OrdinalIgnoreCase))
        {
            score -= 2;
        }
        return score;
    }

    // Explains, in the logs, why a vendor page could not be turned into a silent install.
    // With the exe-extract fallback this only happens when the page offers no .msi/.zip/.exe
    // links at all, so the listing shows what the page did contain.
    private void LogInstallerCandidateDiagnostics(UpdateCandidate candidate, string html)
    {
        var links = new List<string>();
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
        }

        _logger.LogWarning(
            "Vendor page resolve for {SourceUpdateId} ({Device}) found no installable package on {Url} " +
            "({Bytes} bytes, {LinkCount} downloadable link(s): {Links}). Any link listed here was rejected as " +
            "belonging to a different vendor family than the device. The row remains unresolved in the application. " +
            "To install this in-app, the page needs a downloadable .msi/.zip/.exe driver package for this device.",
            candidate.SourceUpdateId, candidate.ForHardwareId, candidate.DownloadUrl, html.Length,
            links.Count, links.Count == 0 ? "none" : string.Join(", ", links));
    }

    internal static bool TryFindInstallerLink(
        Uri page,
        string html,
        out Uri packageUrl,
        out string installerKind,
        Func<Uri, string, bool>? isCompatible = null)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(html);

        Uri? msi = null;
        Uri? zip = null;
        Uri? exe = null;
        Uri? unknownExe = null;
        var exeKind = string.Empty;

        foreach (Match match in HrefPattern().Matches(html))
        {
            var raw = match.Groups["url"].Value;
            if (!Uri.TryCreate(page, raw, out var resolved)
                || resolved.Scheme is not ("http" or "https")
                || IsSourceArchive(resolved))
            {
                continue;
            }

            var extension = Path.GetExtension(resolved.LocalPath);
            if (msi is null && extension.Equals(".msi", StringComparison.OrdinalIgnoreCase))
            {
                if (isCompatible?.Invoke(resolved, "msi-wrapper") == false) { continue; }
                msi = resolved;
            }
            else if (zip is null && extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                if (isCompatible?.Invoke(resolved, "zip-inf") == false) { continue; }
                zip = resolved;
            }
            else if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
            {
                if (exe is null && TryClassifyExe(resolved, out var kind))
                {
                    if (isCompatible?.Invoke(resolved, kind) == false) { continue; }
                    exe = resolved;
                    exeKind = kind;
                }
                else if (unknownExe is null)
                {
                    if (isCompatible?.Invoke(resolved, "exe-extract") == false) { continue; }
                    unknownExe = resolved;
                }
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
        if (unknownExe is not null)
        {
            packageUrl = unknownExe;
            installerKind = "exe-extract";
            return true;
        }

        packageUrl = null!;
        installerKind = string.Empty;
        return false;
    }

    // A deterministic source names the exact page for the exact device, so a package found
    // there is confirmed. An AI lead only names a plausible download page: the package that
    // comes off it is installable and hardware-checked, but the match itself was never proven,
    // so it stays advisory instead of being promoted.
    public static bool IsAiDiscoveryLead(UpdateCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return candidate.SourceUpdateId.StartsWith("ai-latest:", StringComparison.OrdinalIgnoreCase);
    }

    private static UpdateConfidence ResolvedConfidence(UpdateCandidate candidate) =>
        IsAiDiscoveryLead(candidate) ? UpdateConfidence.Advisory : UpdateConfidence.Confirmed;

    public static bool IsPackageCompatibleWithHardware(
        UpdateCandidate candidate,
        Uri packageUrl,
        string installerKind)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(packageUrl);

        var hardwareId = candidate.ForHardwareId;
        var fileName = Path.GetFileName(packageUrl.LocalPath);
        if (installerKind.Equals("nvidia", StringComparison.OrdinalIgnoreCase)
            || packageUrl.Host.Contains("nvidia", StringComparison.OrdinalIgnoreCase))
        {
            return hardwareId.Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase);
        }

        if (installerKind.Equals("amd-chipset", StringComparison.OrdinalIgnoreCase)
            || IsAmdChipsetInstallerFileName(fileName))
        {
            return hardwareId.Contains("VEN_1022", StringComparison.OrdinalIgnoreCase)
                || hardwareId.StartsWith("ACPI\\AMD", StringComparison.OrdinalIgnoreCase)
                || hardwareId.StartsWith("ROOT\\AMD", StringComparison.OrdinalIgnoreCase);
        }

        if (fileName.Contains("adrenalin", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains("minimalsetup", StringComparison.OrdinalIgnoreCase))
        {
            return hardwareId.Contains("VEN_1002", StringComparison.OrdinalIgnoreCase)
                || hardwareId.StartsWith("ROOT\\AMD", StringComparison.OrdinalIgnoreCase)
                || hardwareId.StartsWith("SWD\\DRIVERENUM\\AMD", StringComparison.OrdinalIgnoreCase);
        }

        return true;
    }

    private static bool IsSourceArchive(Uri url) =>
        url.Host.Equals("codeload.github.com", StringComparison.OrdinalIgnoreCase)
        || (url.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            && url.AbsolutePath.Contains("/archive/", StringComparison.OrdinalIgnoreCase));

    // Classified exes run silently with their documented unattended flags. Unclassified
    // exes are still resolved (as "exe-extract", lowest priority): the pipeline never
    // executes them, it unpacks the self-extracting archive and installs any INFs via
    // pnputil, so a GUI-only installer degrades to a skip instead of a hung setup window.
    internal static bool TryClassifyExe(Uri url, out string installerKind)
    {
        var fileName = Path.GetFileName(url.LocalPath);
        if (url.Host.Contains("nvidia", StringComparison.OrdinalIgnoreCase))
        {
            installerKind = "nvidia";
            return true;
        }
        if (IsAmdChipsetInstallerFileName(fileName))
        {
            installerKind = "amd-chipset";
            return true;
        }
        if (fileName.Contains("ViGEmBus", StringComparison.OrdinalIgnoreCase))
        {
            installerKind = "vigem";
            return true;
        }

        installerKind = string.Empty;
        return false;
    }

    private static bool IsAmdChipsetInstallerFileName(string fileName) =>
        fileName.StartsWith("amd_chipset_software_", StringComparison.OrdinalIgnoreCase)
        || fileName.StartsWith("amd_software_", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"href\s*=\s*[""'](?<url>[^""'#]+\.(?:msi|zip|exe)(?:\?[^""']*)?)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex HrefPattern();

    [GeneratedRegex(@"^[A-Za-z0-9_.-]+$")]
    private static partial Regex GitHubNamePattern();
}
