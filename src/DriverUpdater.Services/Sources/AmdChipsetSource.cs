using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using Microsoft.Extensions.Logging;

namespace DriverUpdater.Services.Sources;

public sealed partial class AmdChipsetSource : IUpdateSource
{
    public const string HttpClientName = "AmdChipset";
    internal const string ChipsetSupportUrl = "https://www.amd.com/en/support/chipsets";

    private readonly HttpClient _httpClient;
    private readonly ILogger<AmdChipsetSource> _logger;

    public AmdChipsetSource(HttpClient httpClient, ILogger<AmdChipsetSource> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(logger);
        _httpClient = httpClient;
        _logger = logger;
    }

    public UpdateSource Kind => UpdateSource.Oem;

    public string DisplayName => "AMD Chipset";

    public async IAsyncEnumerable<UpdateCandidate> SearchAsync(
        IReadOnlyCollection<DriverInfo> drivers,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(drivers);

        var matched = drivers.Where(IsSupportedAmdChipsetDriver).ToArray();
        if (matched.Length == 0)
        {
            yield break;
        }

        AmdChipsetRelease? parsed = null;
        try
        {
            var html = await _httpClient.GetStringAsync(new Uri(ChipsetSupportUrl), cancellationToken).ConfigureAwait(false);
            if (TryParseLatestRelease(html, out var release))
            {
                parsed = release;
            }
            else
            {
                _logger.LogWarning("AMD chipset page parse returned no release - HTML layout may have changed");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AMD chipset driver check failed");
        }

        if (parsed is not { } resolved)
        {
            yield break;
        }

        foreach (var driver in matched)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (driver.CurrentDate is { } currentDate && resolved.ReleaseDate <= currentDate)
            {
                continue;
            }

            yield return BuildCandidate(driver, resolved);
        }
    }

    internal static UpdateCandidate BuildCandidate(DriverInfo driver, AmdChipsetRelease release)
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
                SourceUpdateId: $"vendor-installer:installshield:amd-chipset:{release.Version}",
                SupersededIds: Array.Empty<string>(),
                InstallKind: UpdateInstallKind.VendorInstaller);
        }

        return new UpdateCandidate(
            ForHardwareId: driver.HardwareId,
            Source: UpdateSource.Oem,
            NewVersion: DateToVersion(release.ReleaseDate),
            NewDate: release.ReleaseDate,
            DownloadUrl: new Uri(ChipsetSupportUrl),
            SizeBytes: release.SizeBytes ?? 0,
            KbArticle: null,
            IsSuperseded: false,
            SourceUpdateId: $"amd-chipset-page#{release.Version}",
            SupersededIds: Array.Empty<string>(),
            InstallKind: UpdateInstallKind.VendorPage);
    }

    internal static bool IsSupportedAmdChipsetDriver(DriverInfo driver)
    {
        if (driver.Category is not (DriverCategory.Chipset or DriverCategory.System))
        {
            return false;
        }

        return Contains(driver.Provider, "Advanced Micro Devices")
            || Contains(driver.Manufacturer, "Advanced Micro Devices")
            || (Contains(driver.DeviceName, "AMD") && !Contains(driver.DeviceName, "Radeon"));
    }

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

    [GeneratedRegex(@"AMD\s+Chipset\s+Driver[s]?\s+(?<version>\d+(?:\.\d+){2,3})", RegexOptions.IgnoreCase)]
    private static partial Regex ChipsetVersionPattern();

    [GeneratedRegex(@"Release Date\s*</[^>]+>\s*<[^>]+>\s*(?<date>\d{4}-\d{2}-\d{2})", RegexOptions.IgnoreCase)]
    private static partial Regex ReleaseDatePattern();

    [GeneratedRegex(@"File Size\s*</[^>]+>\s*<[^>]+>\s*(?<size>\d+(?:\.\d+)?)\s*MB", RegexOptions.IgnoreCase)]
    private static partial Regex FileSizePattern();

    [GeneratedRegex(@"(?<url>https://drivers\.amd\.com/drivers/[^""'\s<>]*chipset[^""'\s<>]*\.exe)", RegexOptions.IgnoreCase)]
    private static partial Regex DirectInstallerUrlPattern();

    internal readonly record struct AmdChipsetRelease(string Version, DateOnly ReleaseDate, long? SizeBytes, Uri? DirectInstallerUrl = null);
}
