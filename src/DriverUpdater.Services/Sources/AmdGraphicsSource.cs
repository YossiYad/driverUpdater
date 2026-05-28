using System.Globalization;
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

    private readonly HttpClient _httpClient;
    private readonly ILogger<AmdGraphicsSource> _logger;

    public AmdGraphicsSource(HttpClient httpClient, ILogger<AmdGraphicsSource> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(logger);
        _httpClient = httpClient;
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

        if (amdDisplays.Length == 0)
        {
            yield break;
        }

        foreach (var driver in amdDisplays)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryResolveSupportPage(driver, out var supportUri))
            {
                continue;
            }

            AmdReleaseInfo? parsedRelease = null;
            try
            {
                var html = await _httpClient.GetStringAsync(supportUri, cancellationToken).ConfigureAwait(false);
                parsedRelease = TryParseLatestWindowsRelease(html, out var parsed) ? parsed : null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AMD driver check failed for {Device}", driver.DeviceName);
            }

            if (parsedRelease is not { } release || driver.CurrentDate is { } currentDate && release.ReleaseDate <= currentDate)
            {
                continue;
            }

            yield return BuildCandidate(driver, supportUri, release);
        }
    }

    internal static UpdateCandidate BuildCandidate(DriverInfo driver, Uri supportUri, AmdReleaseInfo release)
    {
        if (release.DirectInstallerUrl is { } installerUrl)
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
                SourceUpdateId: $"vendor-installer:installshield:amd-radeon:{release.Revision}",
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
        return false;
    }

    internal static bool TryParseLatestWindowsRelease(string html, out AmdReleaseInfo release)
    {
        ArgumentNullException.ThrowIfNull(html);

        var revisionMatch = AdrenalinRevisionPattern().Match(html);
        var dateMatch = ReleaseDatePattern().Match(html);
        var sizeMatch = FileSizePattern().Match(html);
        var installerMatch = DirectInstallerUrlPattern().Match(html);

        if (!revisionMatch.Success || !dateMatch.Success
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

        release = new AmdReleaseInfo(
            revisionMatch.Groups["revision"].Value.Trim(),
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
