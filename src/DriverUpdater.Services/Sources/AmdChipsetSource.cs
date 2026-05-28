using System.Globalization;
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
            .GroupBy(d => d.HardwareId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
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
                yield return BuildVendorPageCandidate(driver);
            }
            yield break;
        }

        var resolved = parsed.Value;
        _logger.LogInformation(
            "AMD Chipset: parsed release version={Version}, date={Date}, sizeBytes={Size}, directInstaller={HasInstaller}",
            resolved.Version, resolved.ReleaseDate, resolved.SizeBytes ?? 0, resolved.DirectInstallerUrl is not null);

        foreach (var driver in matched)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (driver.CurrentDate is { } currentDate && resolved.ReleaseDate <= currentDate)
            {
                _logger.LogInformation(
                    "AMD Chipset: local driver for {Device} dated {CurrentDate} is already at or newer than upstream {ReleaseDate}; skipping",
                    driver.DeviceName, currentDate, resolved.ReleaseDate);
                continue;
            }

            var candidate = BuildCandidate(driver, resolved);
            _logger.LogInformation(
                "AMD Chipset: yielding {InstallKind} candidate for {Device} -> {Url}",
                candidate.InstallKind, driver.DeviceName, candidate.DownloadUrl);
            yield return candidate;
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
            DownloadUrl: new Uri(ChipsetSupportHubUrl),
            SizeBytes: release.SizeBytes ?? 0,
            KbArticle: null,
            IsSuperseded: false,
            SourceUpdateId: $"amd-chipset-page:{release.Version}",
            SupersededIds: Array.Empty<string>(),
            InstallKind: UpdateInstallKind.VendorPage,
            Confidence: UpdateConfidence.Advisory);
    }

    private static UpdateCandidate BuildVendorPageCandidate(DriverInfo driver)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
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

    internal static bool IsSupportedAmdChipsetDriver(DriverInfo driver)
    {
        if (driver.Category is not (DriverCategory.Chipset or DriverCategory.System))
        {
            return false;
        }

        if (Contains(driver.DeviceName, "Hyper-V") || Contains(driver.DeviceName, "Virtual"))
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

    [GeneratedRegex(@"AMD\s+Chipset\s+Drivers?\s+(?<version>\d+(?:\.\d+){2,3})", RegexOptions.IgnoreCase)]
    private static partial Regex ChipsetVersionPattern();

    [GeneratedRegex(@"Release Date\s*</[^>]+>\s*<[^>]+>\s*(?<date>\d{4}-\d{2}-\d{2})", RegexOptions.IgnoreCase)]
    private static partial Regex ReleaseDatePattern();

    [GeneratedRegex(@"File Size\s*</[^>]+>\s*<[^>]+>\s*(?<size>\d+(?:\.\d+)?)\s*MB", RegexOptions.IgnoreCase)]
    private static partial Regex FileSizePattern();

    [GeneratedRegex(@"(?<url>https://drivers\.amd\.com/drivers/amd_chipset_software_[\d.]+\.exe)", RegexOptions.IgnoreCase)]
    private static partial Regex DirectInstallerUrlPattern();

    internal readonly record struct AmdChipsetRelease(string Version, DateOnly ReleaseDate, long? SizeBytes, Uri? DirectInstallerUrl = null);
}
