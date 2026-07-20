using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using Microsoft.Extensions.Logging;

namespace DriverUpdater.Services.Sources;

public sealed partial class AmdGraphicsSource : IUpdateSource
{
    public const string HttpClientName = "AmdGraphics";
    internal const string AmdSupportUrl = "https://www.amd.com/en/support/download/drivers.html";
    private const string AmdSoftwareDisplayName = "AMD Software";

    private readonly HttpClient _httpClient;
    private readonly IInstalledSoftwareVersionProvider _installedSoftware;
    private readonly ILogger<AmdGraphicsSource> _logger;

    public AmdGraphicsSource(
        HttpClient httpClient,
        IInstalledSoftwareVersionProvider installedSoftware,
        ILogger<AmdGraphicsSource> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(installedSoftware);
        ArgumentNullException.ThrowIfNull(logger);
        _httpClient = httpClient;
        _installedSoftware = installedSoftware;
        _logger = logger;
    }

    public UpdateSource Kind => UpdateSource.Oem;

    public string DisplayName => "AMD Radeon";

    public async IAsyncEnumerable<UpdateCandidate> SearchAsync(
        IReadOnlyCollection<DriverInfo> drivers,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(drivers);

        var amdDisplays = drivers
            .Where(IsSupportedAmdDisplayDriver)
            .GroupBy(d => d.DeviceName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToArray();

        _logger.LogInformation("AMD Radeon source matched {Count} display drivers", amdDisplays.Length);
        if (amdDisplays.Length == 0)
        {
            yield break;
        }

        var installedAmdSoftwareVersion = TryParseVersion(
            _installedSoftware.GetVersion(AmdSoftwareDisplayName));
        if (installedAmdSoftwareVersion is not null)
        {
            _logger.LogInformation(
                "AMD Software {Version} is installed; upstream Adrenalin releases will be compared against the package version",
                installedAmdSoftwareVersion);
        }

        // Phase 1: collect (driver, supportUri, parsedRelease?) per display, sorted so devices with
        // a model-specific page (the discrete RX cards) are fetched first. Their parse result becomes
        // the cached fallback for any device whose page is just a navigation hub (the iGPU "AMD
        // Radeon(TM) Graphics" lands on amd.com/.../drivers.html, which has no Revision/Release block).
        var ordered = amdDisplays
            .Select(d => (Driver: d, Uri: ResolveAndReturn(d), IsSpecific: RadeonRxModelPattern().IsMatch(d.DeviceName)))
            .OrderByDescending(t => t.IsSpecific)
            .ToArray();

        AmdReleaseInfo? cached = null;
        var fetched = new List<(DriverInfo Driver, Uri Uri, AmdReleaseInfo? Release)>(ordered.Length);

        foreach (var (driver, supportUri, _) in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _logger.LogInformation("AMD: fetching support page for {Device}: {Uri}", driver.DeviceName, supportUri);

            AmdReleaseInfo? parsedRelease = null;
            int htmlLength = 0;
            try
            {
                var html = await _httpClient.GetStringAsync(supportUri, cancellationToken).ConfigureAwait(false);
                htmlLength = html.Length;
                parsedRelease = TryParseLatestWindowsRelease(html, out var parsed) ? parsed : null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AMD driver check failed for {Device}", driver.DeviceName);
            }

            if (parsedRelease is null)
            {
                _logger.LogWarning("AMD: parser found no release in {Length}-byte page for {Device}; will reuse cached release if available", htmlLength, driver.DeviceName);
                fetched.Add((driver, supportUri, null));
                continue;
            }

            cached ??= parsedRelease;
            _logger.LogInformation(
                "AMD: parsed release for {Device}: revision={Revision}, date={ReleaseDate}, sizeBytes={Size}, directInstaller={HasInstaller}",
                driver.DeviceName, parsedRelease.Value.Revision, parsedRelease.Value.ReleaseDate, parsedRelease.Value.SizeBytes ?? 0, parsedRelease.Value.DirectInstallerUrl is not null);
            fetched.Add((driver, supportUri, parsedRelease));
        }

        // Phase 2: emit. Unparseable rows reuse the cached release so the iGPU still gets the same
        // VendorInstaller candidate as the discrete card (the Adrenalin bundle covers both).
        foreach (var (driver, supportUri, parsedRelease) in fetched)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var release = parsedRelease ?? cached;
            if (release is null)
            {
                _logger.LogWarning("AMD: no release info available for {Device} (no cache, parser failed); skipping", driver.DeviceName);
                continue;
            }

            var releaseVersion = TryParseVersion(release.Value.Revision);
            if (installedAmdSoftwareVersion is not null
                && releaseVersion is not null
                && releaseVersion <= installedAmdSoftwareVersion)
            {
                _logger.LogInformation(
                    "AMD: installed AMD Software {Installed} already includes upstream Adrenalin {Upstream}; skipping {Device}",
                    installedAmdSoftwareVersion, releaseVersion, driver.DeviceName);
                continue;
            }

            if (driver.CurrentDate is { } currentDate && release.Value.ReleaseDate <= currentDate)
            {
                _logger.LogInformation(
                    "AMD: local driver for {Device} dated {CurrentDate} is already at or newer than upstream {ReleaseDate}; skipping",
                    driver.DeviceName, currentDate, release.Value.ReleaseDate);
                continue;
            }

            var candidate = BuildCandidate(driver, supportUri, release.Value);
            _logger.LogInformation(
                "AMD: yielding {InstallKind} candidate for {Device} -> {Url}",
                candidate.InstallKind, driver.DeviceName, candidate.DownloadUrl);
            yield return candidate;
        }
    }

    private static Uri ResolveAndReturn(DriverInfo driver)
    {
        TryResolveSupportPage(driver, out var uri);
        return uri;
    }

    internal static UpdateCandidate BuildCandidate(DriverInfo driver, Uri supportUri, AmdReleaseInfo release)
    {
        if (release.DirectInstallerUrl is { } installerUrl && !IsWebStub(installerUrl))
        {
            return new UpdateCandidate(
                ForHardwareId: driver.HardwareId,
                Source: UpdateSource.Oem,
                NewVersion: DateToVersion(release.ReleaseDate),
                NewDate: release.ReleaseDate,
                DownloadUrl: installerUrl,
                SizeBytes: release.SizeBytes ?? 0,
                KbArticle: null,
                IsSuperseded: false,
                SourceUpdateId: $"vendor-installer:nullsoft:amd-radeon:{release.Revision}",
                SupersededIds: Array.Empty<string>(),
                InstallKind: UpdateInstallKind.VendorInstaller);
        }

        return new UpdateCandidate(
            ForHardwareId: driver.HardwareId,
            Source: UpdateSource.Oem,
            NewVersion: DateToVersion(release.ReleaseDate),
            NewDate: release.ReleaseDate,
            DownloadUrl: supportUri,
            SizeBytes: release.SizeBytes ?? 0,
            KbArticle: null,
            IsSuperseded: false,
            SourceUpdateId: $"{supportUri}#{release.Revision}",
            SupersededIds: Array.Empty<string>(),
            InstallKind: UpdateInstallKind.VendorPage);
    }

    // The Adrenalin "minimal setup" / "_web" stub is a tiny downloader that always opens
    // its own GUI - /S does not actually run silent. Demote it to VendorPage so the user
    // opens AMD's support page and runs the installer themselves rather than waiting on
    // a silent install that never finishes (we previously saw exit 2 from Setup.exe).
    internal static bool IsWebStub(Uri installerUrl)
    {
        var fileName = Path.GetFileName(installerUrl.LocalPath);
        if (string.IsNullOrEmpty(fileName))
        {
            return false;
        }
        return fileName.Contains("_web", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains("minimalsetup", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsSupportedAmdDisplayDriver(DriverInfo driver) =>
        driver.Category == DriverCategory.Display
        && (Contains(driver.Provider, "Advanced Micro Devices") || Contains(driver.Manufacturer, "Advanced Micro Devices") || Contains(driver.DeviceName, "AMD Radeon"))
        && Contains(driver.DeviceName, "Radeon");

    internal static bool TryResolveSupportPage(DriverInfo driver, out Uri supportUri)
    {
        var match = RadeonRxModelPattern().Match(driver.DeviceName);
        if (match.Success)
        {
            var modelNumber = match.Groups["model"].Value;
            var suffix = match.Groups["suffix"].Value;
            var series = $"{modelNumber[0]}000";
            var slug = $"amd-radeon-rx-{modelNumber}{(string.IsNullOrWhiteSpace(suffix) ? string.Empty : "-" + suffix.ToLowerInvariant())}";
            supportUri = new Uri($"https://www.amd.com/en/support/downloads/drivers.html/graphics/radeon-rx/radeon-rx-{series}-series/{slug}.html");
            return true;
        }

        supportUri = new Uri(AmdSupportUrl);
        return true;
    }

    internal static bool TryParseLatestWindowsRelease(string html, out AmdReleaseInfo release)
    {
        ArgumentNullException.ThrowIfNull(html);

        var revisionMatch = AdrenalinRevisionPattern().Match(html);
        var dateMatch = ReleaseDatePattern().Match(html);
        var sizeMatch = FileSizePattern().Match(html);

        if (!revisionMatch.Success || !dateMatch.Success
            || !DateOnly.TryParseExact(dateMatch.Groups["date"].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var releaseDate))
        {
            release = default;
            return false;
        }

        release = new AmdReleaseInfo(
            revisionMatch.Groups["revision"].Value.Trim(),
            releaseDate,
            ParseSizeBytes(sizeMatch.Success ? sizeMatch.Groups["size"].Value : null),
            ChooseInstallerUrl(html));
        return true;
    }

    // AMD's Adrenalin support pages typically list both a full installer (~800MB, NSIS,
    // installs silent with /S) and a small _web / minimalsetup stub (~10MB, always opens
    // GUI). We want the full installer so silent install actually works. Look at every
    // .exe href in the page, prefer one that is not a web stub, and only fall through
    // to the stub if that is all the page offers (the downstream IsWebStub check will
    // then demote the candidate to VendorPage).
    internal static Uri? ChooseInstallerUrl(string html)
    {
        Uri? firstAny = null;
        foreach (Match m in DirectInstallerUrlPattern().Matches(html))
        {
            if (!Uri.TryCreate(m.Groups["url"].Value, UriKind.Absolute, out var parsed))
            {
                continue;
            }
            if (parsed.Scheme is not "http" and not "https")
            {
                continue;
            }
            firstAny ??= parsed;
            if (!IsWebStub(parsed))
            {
                return parsed;
            }
        }
        return firstAny;
    }

    internal static Version DateToVersion(DateOnly date) => new(date.Year, date.Month, date.Day, 0);

    private static Version? TryParseVersion(string? value) =>
        Version.TryParse(value, out var version) ? version : null;

    private static bool Contains(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static long? ParseSizeBytes(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || !double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var sizeMb))
        {
            return null;
        }

        return (long)(sizeMb * 1024 * 1024);
    }

    [GeneratedRegex(@"Adrenalin\s+(?<revision>\d+(?:\.\d+){1,3})(?:\s*\([^)]+\))?", RegexOptions.IgnoreCase)]
    private static partial Regex AdrenalinRevisionPattern();

    [GeneratedRegex(@"Release Date\s*</[^>]+>\s*<[^>]+>\s*(?<date>\d{4}-\d{2}-\d{2})", RegexOptions.IgnoreCase)]
    private static partial Regex ReleaseDatePattern();

    [GeneratedRegex(@"File Size\s*</[^>]+>\s*<[^>]+>\s*(?<size>\d+(?:\.\d+)?)\s*MB", RegexOptions.IgnoreCase)]
    private static partial Regex FileSizePattern();

    [GeneratedRegex(@"\bRX\s+(?<model>[5-9]\d{3})(?:\s*(?<suffix>XT|XTX))?\b", RegexOptions.IgnoreCase)]
    private static partial Regex RadeonRxModelPattern();

    [GeneratedRegex(@"(?<url>https://drivers\.amd\.com/drivers/[^""'\s<>]+\.exe)", RegexOptions.IgnoreCase)]
    private static partial Regex DirectInstallerUrlPattern();

    internal readonly record struct AmdReleaseInfo(string Revision, DateOnly ReleaseDate, long? SizeBytes, Uri? DirectInstallerUrl = null);
}
