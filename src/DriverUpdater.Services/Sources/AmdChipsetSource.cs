using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using DriverUpdater.Services.Sources.Internal;
using Microsoft.Extensions.Logging;

namespace DriverUpdater.Services.Sources;

public sealed partial class AmdChipsetSource : IUpdateSource
{
    public const string HttpClientName = "AmdChipset";
    internal const string ChipsetSupportHubUrl = "https://www.amd.com/en/support/chipsets";

    private readonly HttpClient _httpClient;
    private readonly IAmdSocketDetector _socketDetector;
    private readonly TimeProvider _clock;
    private readonly ILogger<AmdChipsetSource> _logger;

    public AmdChipsetSource(
        HttpClient httpClient,
        IAmdSocketDetector socketDetector,
        ILogger<AmdChipsetSource> logger,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(socketDetector);
        ArgumentNullException.ThrowIfNull(logger);
        _httpClient = httpClient;
        _socketDetector = socketDetector;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    public UpdateSource Kind => UpdateSource.Oem;

    public string DisplayName => "AMD Chipset";

    public async IAsyncEnumerable<UpdateCandidate> SearchAsync(
        IReadOnlyCollection<DriverInfo> drivers,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(drivers);

        var matched = drivers.Where(IsSupportedAmdChipsetDriver)
            .DistinctBy(d => d.DeviceId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        _logger.LogInformation("AMD Chipset source matched {Count} chipset/system drivers", matched.Length);
        if (matched.Length == 0)
        {
            yield break;
        }

        var socket = await _socketDetector.DetectAsync(cancellationToken).ConfigureAwait(false);
        var supportUri = new Uri($"https://www.amd.com/en/support/downloads/drivers.html/chipsets/{socket.Socket}/{socket.ChipsetSlug}.html");
        _logger.LogInformation("AMD Chipset: fetching {Uri}", supportUri);

        AmdChipsetRelease? parsed = null;
        int htmlLength = 0;
        try
        {
            var html = await _httpClient.GetStringAsync(supportUri, cancellationToken).ConfigureAwait(false);
            htmlLength = html.Length;
            parsed = TryParseLatestRelease(html, out var release) ? release : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AMD chipset driver check failed");
        }

        if (parsed is null)
        {
            _logger.LogWarning("AMD Chipset: parser found no release in {Length}-byte page - HTML layout may have changed; falling back to vendor page", htmlLength);
            foreach (var driver in matched)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return BuildVendorPageCandidate(
                    driver,
                    DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime));
            }
            yield break;
        }

        var resolved = parsed.Value;
        _logger.LogInformation(
            "AMD Chipset: parsed release version={Version}, date={Date}, sizeBytes={Size}, directInstaller={HasInstaller}",
            resolved.Version, resolved.ReleaseDate, resolved.SizeBytes ?? 0, resolved.DirectInstallerUrl is not null);

        string? releaseNotesHtml = null;
        try
        {
            var releaseNotesUri = BuildReleaseNotesUri(resolved.Version);
            _logger.LogInformation("AMD Chipset: fetching component versions from {Uri}", releaseNotesUri);
            releaseNotesHtml = await _httpClient.GetStringAsync(releaseNotesUri, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "AMD Chipset: component manifest could not be read. The package will not be offered because its bundle version cannot be compared safely with individual driver versions.");
        }

        foreach (var driver in matched)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (releaseNotesHtml is null
                || !TryFindComponentVersion(releaseNotesHtml, driver, out var componentVersion))
            {
                _logger.LogInformation(
                    "AMD Chipset: no authoritative component version was found for {Device}; skipping the package-level comparison",
                    driver.DeviceName);
                continue;
            }

            if (driver.CurrentVersion is { } currentVersion && currentVersion >= componentVersion)
            {
                _logger.LogInformation(
                    "AMD Chipset: {Device} is already current at {CurrentVersion}; package {PackageVersion} contains {ComponentVersion}",
                    driver.DeviceName, currentVersion, resolved.Version, componentVersion);
                continue;
            }

            var candidate = BuildCandidate(driver, resolved, componentVersion);
            _logger.LogInformation(
                "AMD Chipset: yielding {InstallKind} candidate for {Device} -> {Url}",
                candidate.InstallKind, driver.DeviceName, candidate.DownloadUrl);
            yield return candidate;
        }
    }

    internal static UpdateCandidate BuildCandidate(
        DriverInfo driver,
        AmdChipsetRelease release,
        Version componentVersion)
    {
        if (release.DirectInstallerUrl is { } installerUrl)
        {
            return new UpdateCandidate(
                ForHardwareId: driver.HardwareId,
                Source: UpdateSource.Oem,
                NewVersion: componentVersion,
                NewDate: release.ReleaseDate,
                DownloadUrl: installerUrl,
                SizeBytes: release.SizeBytes ?? 0,
                KbArticle: null,
                IsSuperseded: false,
                SourceUpdateId: $"vendor-installer:amd-chipset:{release.Version}",
                SupersededIds: Array.Empty<string>(),
                InstallKind: UpdateInstallKind.VendorInstaller);
        }

        return new UpdateCandidate(
            ForHardwareId: driver.HardwareId,
            Source: UpdateSource.Oem,
            NewVersion: componentVersion,
            NewDate: release.ReleaseDate,
            DownloadUrl: new Uri(ChipsetSupportHubUrl),
            SizeBytes: release.SizeBytes ?? 0,
            KbArticle: null,
            IsSuperseded: false,
            SourceUpdateId: $"amd-chipset-page:{release.Version}",
            SupersededIds: Array.Empty<string>(),
            InstallKind: UpdateInstallKind.VendorPage,
            Confidence: UpdateConfidence.Advisory);
    }

    private static UpdateCandidate BuildVendorPageCandidate(DriverInfo driver, DateOnly today)
    {
        return new UpdateCandidate(
            ForHardwareId: driver.HardwareId,
            Source: UpdateSource.Oem,
            NewVersion: new Version(today.Year, today.Month, today.Day, 0),
            NewDate: today,
            DownloadUrl: new Uri(ChipsetSupportHubUrl),
            SizeBytes: 0,
            KbArticle: null,
            IsSuperseded: false,
            SourceUpdateId: $"amd-chipset-page:{driver.HardwareId}",
            SupersededIds: Array.Empty<string>(),
            InstallKind: UpdateInstallKind.VendorPage,
            Confidence: UpdateConfidence.Advisory);
    }

    public static bool IsSupportedAmdChipsetDriver(DriverInfo driver)
    {
        if (driver.Category is not (DriverCategory.Chipset or DriverCategory.System))
        {
            return false;
        }

        if (Contains(driver.DeviceName, "Hyper-V") || Contains(driver.DeviceName, "Virtual"))
        {
            return false;
        }

        var isAmd = Contains(driver.Provider, "Advanced Micro Devices")
            || Contains(driver.Manufacturer, "Advanced Micro Devices")
            || Contains(driver.DeviceName, "AMD");
        return isAmd && ComponentMarkers(driver).Count > 0;
    }

    internal static bool TryFindComponentVersion(string html, DriverInfo driver, out Version version)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(driver);

        var text = WebUtility.HtmlDecode(HtmlTagPattern().Replace(html, " "));
        text = WhitespacePattern().Replace(text, " ");
        foreach (var marker in ComponentMarkers(driver))
        {
            var markerIndex = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0)
            {
                continue;
            }

            var section = text.Substring(markerIndex, Math.Min(300, text.Length - markerIndex));
            var versions = DriverVersionPattern().Matches(section)
                .Take(2)
                .Select(m => Version.TryParse(m.Value, out var parsed) ? parsed : null)
                .Where(v => v is not null)
                .Cast<Version>()
                .Distinct()
                .ToArray();
            if (versions.Length == 0)
            {
                continue;
            }

            var sameFamily = driver.CurrentVersion is { } current
                ? versions.Where(v => v.Major == current.Major).ToArray()
                : Array.Empty<Version>();
            version = (sameFamily.Length > 0 ? sameFamily : versions).Max()!;
            return true;
        }

        version = null!;
        return false;
    }

    private static IReadOnlyList<string> ComponentMarkers(DriverInfo driver)
    {
        var name = driver.DeviceName;
        if (Contains(name, "SFH I2C")) { return ["AMD SFH I2C Driver"]; }
        if (Contains(name, "I2C")) { return ["AMD I2C Driver"]; }
        if (Contains(name, "GPIO")) { return ["AMD GPIO2 Driver", "PT GPIO Driver"]; }
        if (Contains(name, "Provisioning Packages") || Contains(name, "PPM Provisioning")) { return ["AMD PPM Provisioning File Driver"]; }
        if (Contains(name, "3D V-Cache")) { return ["AMD 3D V-Cache Performance Optimizer Driver"]; }
        if (Contains(name, "Application Compatibility Database")) { return ["AMD Application Compatibility Database Driver"]; }
        if (Contains(name, "SMBUS")) { return ["AMD Interface Driver", "AMD SMBUS Driver"]; }
        if (Contains(name, "PCI")) { return ["AMD Interface Driver", "AMD PCI Device Driver"]; }
        if (Contains(name, "UART")) { return ["AMD UART Driver"]; }
        if (Contains(name, "PSP")) { return ["AMD PSP Driver"]; }
        if (Contains(name, "IOV")) { return ["AMD IOV Driver"]; }
        if (Contains(name, "AS4 ACPI")) { return ["AMD AS4 ACPI Driver"]; }
        if (Contains(name, "SFH")) { return ["AMD SFH Driver", "AMD SFH1.1 Driver"]; }
        if (Contains(name, "USB Filter")) { return ["AMD USB Filter Driver"]; }
        if (Contains(name, "USB4")) { return ["AMD USB4 CM Driver"]; }
        if (Contains(name, "CIR")) { return ["AMD CIR Driver"]; }
        if (Contains(name, "MicroPEP")) { return ["AMD MicroPEP Driver"]; }
        if (Contains(name, "Wireless Button")) { return ["AMD Wireless Button Driver"]; }
        if (Contains(name, "AMS Mailbox")) { return ["AMD AMS Mailbox Driver"]; }
        if (Contains(name, "S0i3")) { return ["AMD S0i3 Filter Driver"]; }
        if (Contains(name, "HSMP")) { return ["AMD HSMP Driver"]; }
        if (Contains(name, "PMF")) { return ["AMD PMF-8000Series Driver", "AMD PMF-7040 Series Driver", "AMD PMF-6000 Series Driver"]; }
        return Array.Empty<string>();
    }

    private static Uri BuildReleaseNotesUri(string version) =>
        new($"https://www.amd.com/en/resources/support-articles/release-notes/RN-RYZEN-CHIPSET-{version.Replace('.', '-')}.html");

    internal static bool TryParseLatestRelease(string html, out AmdChipsetRelease release)
    {
        ArgumentNullException.ThrowIfNull(html);

        var versionMatch = ChipsetVersionPattern().Match(html);
        var dateMatch = ReleaseDatePattern().Match(html);
        var sizeMatch = FileSizePattern().Match(html);
        var installerMatch = DirectInstallerUrlPattern().Match(html);

        if (!versionMatch.Success || !dateMatch.Success
            || !DateOnly.TryParseExact(dateMatch.Groups["date"].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var releaseDate))
        {
            release = default;
            return false;
        }

        Uri? installerUrl = null;
        if (installerMatch.Success
            && Uri.TryCreate(installerMatch.Groups["url"].Value, UriKind.Absolute, out var parsed)
            && parsed.Scheme is "http" or "https")
        {
            installerUrl = parsed;
        }

        release = new AmdChipsetRelease(
            versionMatch.Groups["version"].Value.Trim(),
            releaseDate,
            ParseSizeBytes(sizeMatch.Success ? sizeMatch.Groups["size"].Value : null),
            installerUrl);
        return true;
    }

    internal static Version DateToVersion(DateOnly date) => new(date.Year, date.Month, date.Day, 0);

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

    [GeneratedRegex(@"Revision Number\s*</[^>]+>\s*<[^>]+>\s*(?<version>\d+(?:\.\d+){2,3})", RegexOptions.IgnoreCase)]
    private static partial Regex ChipsetVersionPattern();

    [GeneratedRegex(@"Release Date\s*</[^>]+>\s*<[^>]+>\s*(?<date>\d{4}-\d{2}-\d{2})", RegexOptions.IgnoreCase)]
    private static partial Regex ReleaseDatePattern();

    [GeneratedRegex(@"File Size\s*</[^>]+>\s*<[^>]+>\s*(?<size>\d+(?:\.\d+)?)\s*MB", RegexOptions.IgnoreCase)]
    private static partial Regex FileSizePattern();

    [GeneratedRegex(@"(?<url>https://drivers\.amd\.com/drivers/amd_chipset_software_[\d.]+\.exe)", RegexOptions.IgnoreCase)]
    private static partial Regex DirectInstallerUrlPattern();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTagPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();

    [GeneratedRegex(@"\b\d+(?:\.\d+){2,3}\b")]
    private static partial Regex DriverVersionPattern();

    internal readonly record struct AmdChipsetRelease(string Version, DateOnly ReleaseDate, long? SizeBytes, Uri? DirectInstallerUrl = null);
}
