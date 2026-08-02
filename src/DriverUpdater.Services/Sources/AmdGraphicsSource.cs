using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using Microsoft.Extensions.Logging;

namespace DriverUpdater.Services.Sources;

public sealed partial class AmdGraphicsSource : IUpdateSource
{
    public const string HttpClientName = "AmdGraphics";
    internal const string AmdSupportUrl = "https://www.amd.com/en/support/download/drivers.html";
    internal const string AmdVersionTableUrl = "https://raw.githubusercontent.com/GPUOpen-Drivers/amd-vulkan-versions/master/amdversions.xml";
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

        string? versionTable = null;
        try
        {
            versionTable = await _httpClient.GetStringAsync(AmdVersionTableUrl, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AMD machine-readable version table request failed; falling back to support pages");
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
                if (parsedRelease is { } pageRelease
                    && !string.IsNullOrWhiteSpace(versionTable)
                    && TryParseVersionTable(versionTable, ClassifyArchitecture(driver), out var feedRelease)
                    && string.Equals(pageRelease.Revision, feedRelease.Revision, StringComparison.OrdinalIgnoreCase))
                {
                    parsedRelease = pageRelease with
                    {
                        DriverVersion = feedRelease.DriverVersion,
                        ReleaseNotesUrl = feedRelease.ReleaseNotesUrl
                    };
                }
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
                NewVersion: release.DriverVersion ?? DateToVersion(release.ReleaseDate),
                NewDate: release.ReleaseDate,
                DownloadUrl: installerUrl,
                SizeBytes: release.SizeBytes ?? 0,
                KbArticle: null,
                IsSuperseded: false,
                SourceUpdateId: $"vendor-installer:nullsoft:amd-radeon:{release.Revision}",
                SupersededIds: Array.Empty<string>(),
                InstallKind: UpdateInstallKind.VendorInstaller,
                VersionLabel: LabelFor(release));
        }

        return new UpdateCandidate(
            ForHardwareId: driver.HardwareId,
            Source: UpdateSource.Oem,
            NewVersion: release.DriverVersion ?? DateToVersion(release.ReleaseDate),
            NewDate: release.ReleaseDate,
            DownloadUrl: supportUri,
            SizeBytes: release.SizeBytes ?? 0,
            KbArticle: null,
            IsSuperseded: false,
            SourceUpdateId: $"{supportUri}#{release.Revision}",
            SupersededIds: Array.Empty<string>(),
            InstallKind: UpdateInstallKind.VendorPage,
            VersionLabel: LabelFor(release));
    }

    // Adrenalin releases are branded by revision ("25.8.1"). When AMD does not also publish the
    // INF driver version, NewVersion is only a date stand-in, so show the branded revision.
    private static string? LabelFor(AmdReleaseInfo release) =>
        release.DriverVersion is null && !string.IsNullOrWhiteSpace(release.Revision)
            ? release.Revision
            : null;

    // The Adrenalin "minimal setup" / "_web" stub is a tiny downloader that always opens
    // its own GUI. /S does not actually run silent. Demote it to VendorPage so the in-app
    // resolver can look for a full package without launching the web stub.
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

    internal static AmdGraphicsArchitecture ClassifyArchitecture(DriverInfo driver)
    {
        if (TryExtractAmdDeviceId(driver, out var deviceId))
        {
            if (Rdna12DeviceIds.Contains(deviceId))
            {
                return AmdGraphicsArchitecture.Rdna12;
            }
            if (PolarisVegaDeviceIds.Contains(deviceId))
            {
                return AmdGraphicsArchitecture.PolarisVega;
            }
        }

        var model = driver.DeviceName;
        if (ContainsAny(model, "RX 5500", "RX 5600", "RX 5700", "RX 6400", "RX 6500", "RX 6600", "RX 6700", "RX 6800", "RX 6900"))
        {
            return AmdGraphicsArchitecture.Rdna12;
        }
        if (ContainsAny(model, "Vega", "RX 460", "RX 470", "RX 480", "RX 550", "RX 560", "RX 570", "RX 580", "RX 590"))
        {
            return AmdGraphicsArchitecture.PolarisVega;
        }
        return AmdGraphicsArchitecture.Mainstream;
    }

    internal static bool TryParseVersionTable(
        string xml,
        AmdGraphicsArchitecture architecture,
        out AmdReleaseInfo release)
    {
        ArgumentNullException.ThrowIfNull(xml);
        release = default;

        try
        {
            var document = XDocument.Parse(xml, LoadOptions.None);
            foreach (var driver in document.Descendants("driver"))
            {
                if (!string.Equals(driver.Attribute("operating-system")?.Value, "Windows", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var versionAttribute = driver.Attribute("version")?.Value;
                if (string.IsNullOrWhiteSpace(versionAttribute)
                    || ClassifyVersionBranch(versionAttribute) != architecture)
                {
                    continue;
                }

                var publicVersion = versionAttribute.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                var driverVersionRaw = driver.Element("windows-version")?.Value;
                var releaseDateRaw = driver.Element("release-date")?.Value;
                if (string.IsNullOrWhiteSpace(publicVersion)
                    || !Version.TryParse(driverVersionRaw, out var driverVersion)
                    || !DateOnly.TryParseExact(releaseDateRaw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var releaseDate))
                {
                    continue;
                }

                var releaseNotes = TryCreateAmdUri(driver.Element("download-url")?.Value);
                release = new AmdReleaseInfo(
                    publicVersion,
                    releaseDate,
                    SizeBytes: null,
                    DirectInstallerUrl: null,
                    DriverVersion: driverVersion,
                    ReleaseNotesUrl: releaseNotes);
                return true;
            }

            return false;
        }
        catch (System.Xml.XmlException)
        {
            return false;
        }
    }

    private static AmdGraphicsArchitecture ClassifyVersionBranch(string versionAttribute)
    {
        if (versionAttribute.Contains("Polaris and Vega", StringComparison.OrdinalIgnoreCase))
        {
            return AmdGraphicsArchitecture.PolarisVega;
        }
        if (versionAttribute.Contains("RDNA1 and RDNA2", StringComparison.OrdinalIgnoreCase))
        {
            return AmdGraphicsArchitecture.Rdna12;
        }
        return AmdGraphicsArchitecture.Mainstream;
    }

    private static Uri? TryCreateAmdUri(string? raw)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !(uri.Host.Equals("amd.com", StringComparison.OrdinalIgnoreCase)
                || uri.Host.EndsWith(".amd.com", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }
        return uri;
    }

    private static bool TryExtractAmdDeviceId(DriverInfo driver, out ushort deviceId)
    {
        foreach (var hardwareId in driver.HardwareIds.Append(driver.HardwareId).Append(driver.DeviceId))
        {
            var match = AmdDeviceIdPattern().Match(hardwareId ?? string.Empty);
            if (match.Success
                && ushort.TryParse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out deviceId))
            {
                return true;
            }
        }
        deviceId = 0;
        return false;
    }

    private static bool ContainsAny(string value, params string[] needles) =>
        needles.Any(needle => value.Contains(needle, StringComparison.OrdinalIgnoreCase));

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

    [GeneratedRegex(@"VEN_1002&DEV_([0-9A-F]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex AmdDeviceIdPattern();

    private static readonly HashSet<ushort> Rdna12DeviceIds =
    [
        0x7310, 0x7312, 0x7318, 0x7319, 0x731A, 0x731B, 0x731E, 0x731F, 0x7340, 0x7341, 0x7347, 0x734F,
        0x7360, 0x7362, 0x73A0, 0x73A1, 0x73A2, 0x73A3, 0x73A5, 0x73AB, 0x73AE, 0x73AF, 0x73BF, 0x73D0,
        0x73DF, 0x73E0, 0x73E1, 0x73E3, 0x73E8, 0x73E9, 0x73EF, 0x73FF, 0x7420, 0x7421, 0x7422, 0x7423,
        0x743F
    ];

    private static readonly HashSet<ushort> PolarisVegaDeviceIds =
    [
        0x67C0, 0x67C2, 0x67C4, 0x67C7, 0x67CA, 0x67CC, 0x67CF, 0x67D0, 0x67D4, 0x67D7, 0x67DF, 0x6FDF,
        0x67E0, 0x67E1, 0x67E3, 0x67E8, 0x67EB, 0x67EF, 0x67FF, 0x6980, 0x6981, 0x6985, 0x6986, 0x6987,
        0x698F, 0x699F, 0x6860, 0x6861, 0x6862, 0x6863, 0x6864, 0x6867, 0x6868, 0x686C, 0x687F, 0x69A0,
        0x69A1, 0x69A2, 0x69A3, 0x69AF, 0x66A0, 0x66A1, 0x66A2, 0x66A3, 0x66A7, 0x66AF
    ];

    internal enum AmdGraphicsArchitecture
    {
        Mainstream,
        Rdna12,
        PolarisVega
    }

    internal readonly record struct AmdReleaseInfo(
        string Revision,
        DateOnly ReleaseDate,
        long? SizeBytes,
        Uri? DirectInstallerUrl = null,
        Version? DriverVersion = null,
        Uri? ReleaseNotesUrl = null);
}
