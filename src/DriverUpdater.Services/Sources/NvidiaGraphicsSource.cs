using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using Microsoft.Extensions.Logging;

namespace DriverUpdater.Services.Sources;

public sealed class NvidiaGraphicsSource : IUpdateSource
{
    public const string HttpClientName = "NvidiaGraphics";

    // GeForce RTX 50 Series desktop / RTX 5090 - returns the Game Ready Driver that
    // covers every consumer GeForce card from GTX 10 onwards in a single query.
    internal const int LatestGeForcePsid = 131;
    internal const int LatestGeForcePfid = 1066;
    internal const int OsIdWin11x64 = 57;

    internal const string ApiPath = "/services_toolkit/services/com/nvidia/services/AjaxDriverService.php";

    private readonly HttpClient _httpClient;
    private readonly ILogger<NvidiaGraphicsSource> _logger;

    public NvidiaGraphicsSource(HttpClient httpClient, ILogger<NvidiaGraphicsSource> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(logger);
        _httpClient = httpClient;
        _logger = logger;
    }

    public UpdateSource Kind => UpdateSource.Oem;

    public string DisplayName => "NVIDIA GeForce";

    public async IAsyncEnumerable<UpdateCandidate> SearchAsync(
        IReadOnlyCollection<DriverInfo> drivers,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(drivers);

        var nvidiaGpus = drivers
            .Where(IsSupportedNvidiaGpu)
            .GroupBy(d => d.HardwareId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToArray();

        _logger.LogInformation("NVIDIA GeForce source matched {Count} GPUs", nvidiaGpus.Length);
        if (nvidiaGpus.Length == 0)
        {
            yield break;
        }

        var requestUri = BuildApiUri();
        _logger.LogInformation("NVIDIA: fetching driver metadata from {Uri}", requestUri);

        NvidiaRelease? release = null;
        int responseLength = 0;
        try
        {
            var json = await _httpClient.GetStringAsync(requestUri, cancellationToken).ConfigureAwait(false);
            responseLength = json.Length;
            release = TryParseLatestRelease(json, out var parsed) ? parsed : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "NVIDIA driver check failed");
        }

        if (release is null)
        {
            _logger.LogWarning("NVIDIA: parser found no release in {Length}-byte response - API format may have changed", responseLength);
            yield break;
        }

        var resolved = release.Value;
        _logger.LogInformation(
            "NVIDIA: parsed release version={Version}, date={Date}, sizeBytes={Size}, url={Url}",
            resolved.Version, resolved.ReleaseDate, resolved.SizeBytes ?? 0, resolved.DownloadUrl);

        foreach (var driver in nvidiaGpus)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (driver.CurrentDate is { } currentDate && resolved.ReleaseDate <= currentDate)
            {
                _logger.LogInformation(
                    "NVIDIA: local driver for {Device} dated {CurrentDate} is already at or newer than upstream {ReleaseDate}; skipping",
                    driver.DeviceName, currentDate, resolved.ReleaseDate);
                continue;
            }

            var candidate = BuildCandidate(driver, resolved);
            _logger.LogInformation(
                "NVIDIA: yielding {InstallKind} candidate for {Device} -> {Url}",
                candidate.InstallKind, driver.DeviceName, candidate.DownloadUrl);
            yield return candidate;
        }
    }

    internal static Uri BuildApiUri() => new(
        $"{ApiPath}?func=DriverManualLookup"
        + $"&psid={LatestGeForcePsid}"
        + $"&pfid={LatestGeForcePfid}"
        + $"&osID={OsIdWin11x64}"
        + "&languageCode=1033&isWHQL=1&dch=1&sort1=0&numberOfResults=1",
        UriKind.Relative);

    internal static UpdateCandidate BuildCandidate(DriverInfo driver, NvidiaRelease release) =>
        new(
            ForHardwareId: driver.HardwareId,
            Source: UpdateSource.Oem,
            NewVersion: DateToVersion(release.ReleaseDate),
            NewDate: release.ReleaseDate,
            DownloadUrl: release.DownloadUrl,
            SizeBytes: release.SizeBytes ?? 0,
            KbArticle: null,
            IsSuperseded: false,
            SourceUpdateId: $"vendor-installer:nvidia:{release.Version}",
            SupersededIds: Array.Empty<string>(),
            InstallKind: UpdateInstallKind.VendorInstaller);

    internal static bool IsSupportedNvidiaGpu(DriverInfo driver)
    {
        if (driver.Category != DriverCategory.Display)
        {
            return false;
        }

        if (Contains(driver.DeviceName, "Quadro") || Contains(driver.DeviceName, "Tesla"))
        {
            return false;
        }

        return Contains(driver.Provider, "NVIDIA")
            || Contains(driver.Manufacturer, "NVIDIA")
            || Contains(driver.DeviceName, "NVIDIA")
            || Contains(driver.DeviceName, "GeForce")
            || Contains(driver.DeviceName, "RTX")
            || Contains(driver.DeviceName, "GTX");
    }

    internal static bool TryParseLatestRelease(string json, out NvidiaRelease release)
    {
        ArgumentNullException.ThrowIfNull(json);
        release = default;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("IDS", out var ids)
                || ids.ValueKind != JsonValueKind.Array
                || ids.GetArrayLength() == 0)
            {
                return false;
            }

            var first = ids[0];
            if (!first.TryGetProperty("downloadInfo", out var info))
            {
                return false;
            }

            var version = info.TryGetProperty("Version", out var v) ? v.GetString() : null;
            var dateRaw = info.TryGetProperty("ReleaseDateTime", out var d) ? Uri.UnescapeDataString(d.GetString() ?? string.Empty) : null;
            var urlRaw = info.TryGetProperty("DownloadURL", out var u) ? u.GetString() : null;
            var sizeRaw = info.TryGetProperty("DownloadURLFileSize", out var s) ? s.GetString() : null;

            if (string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(dateRaw) || string.IsNullOrWhiteSpace(urlRaw))
            {
                return false;
            }

            if (!TryParseNvidiaDate(dateRaw, out var releaseDate))
            {
                return false;
            }

            if (!Uri.TryCreate(urlRaw, UriKind.Absolute, out var downloadUrl) || downloadUrl.Scheme is not ("http" or "https"))
            {
                return false;
            }

            release = new NvidiaRelease(version.Trim(), releaseDate, downloadUrl, ParseSizeBytes(sizeRaw));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static bool TryParseNvidiaDate(string raw, out DateOnly date)
    {
        string[] formats = ["MMM d, yyyy", "MMM dd, yyyy"];
        var trimmed = raw.Trim();
        if (DateOnly.TryParseExact(trimmed, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
        {
            return true;
        }

        // NVIDIA returns "Tue May 26, 2026" - the day-of-week prefix is locale-fragile, so
        // strip the leading token and retry. Only done as a fallback so a weekday-less
        // "May 26, 2026" still parses via the direct attempt above.
        var firstSpace = trimmed.IndexOf(' ');
        if (firstSpace <= 0)
        {
            return false;
        }
        var withoutWeekday = trimmed[(firstSpace + 1)..].Trim();
        return DateOnly.TryParseExact(withoutWeekday, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    internal static Version DateToVersion(DateOnly date) => new(date.Year, date.Month, date.Day, 0);

    private static bool Contains(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static long? ParseSizeBytes(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var trimmed = raw.Trim();
        var spaceIdx = trimmed.IndexOf(' ');
        if (spaceIdx <= 0)
        {
            return null;
        }

        if (!double.TryParse(trimmed[..spaceIdx], NumberStyles.Float, CultureInfo.InvariantCulture, out var amount))
        {
            return null;
        }

        var unit = trimmed[(spaceIdx + 1)..].Trim();
        var multiplier = unit.ToUpperInvariant() switch
        {
            "GB" => 1024L * 1024L * 1024L,
            "MB" => 1024L * 1024L,
            "KB" => 1024L,
            _ => 0L
        };
        return multiplier == 0 ? null : (long)(amount * multiplier);
    }

    internal readonly record struct NvidiaRelease(string Version, DateOnly ReleaseDate, Uri DownloadUrl, long? SizeBytes);
}
