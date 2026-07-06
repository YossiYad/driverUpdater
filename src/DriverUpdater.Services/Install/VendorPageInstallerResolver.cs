using System.IO;
using System.Text.RegularExpressions;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using Microsoft.Extensions.Logging;

namespace DriverUpdater.Services.Install;

public sealed partial class VendorPageInstallerResolver : IVendorPageInstallerResolver
{
    public const string HttpClientName = "VendorPageResolver";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<VendorPageInstallerResolver> _logger;

    public VendorPageInstallerResolver(IHttpClientFactory httpClientFactory, ILogger<VendorPageInstallerResolver> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(logger);
        _httpClientFactory = httpClientFactory;
        _logger = logger;
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

        string html;
        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            html = await client.GetStringAsync(candidate.DownloadUrl, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Vendor page resolve failed for {SourceUpdateId}: could not fetch {Url}",
                candidate.SourceUpdateId, candidate.DownloadUrl);
            return null;
        }

        if (!TryFindInstallerLink(candidate.DownloadUrl, html, out var packageUrl, out var installerKind))
        {
            _logger.LogInformation(
                "Vendor page resolve for {SourceUpdateId}: no direct installer link found on {Url} ({Length} bytes scanned)",
                candidate.SourceUpdateId, candidate.DownloadUrl, html.Length);
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
