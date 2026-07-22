using System.Globalization;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using Microsoft.Extensions.Logging;

namespace DriverUpdater.Services.Sources;

public sealed partial class IntelGraphicsSource : IUpdateSource
{
    public const string HttpClientName = "IntelGraphics";
    internal const string CatalogPath = "data/en";
    internal const string ConfigurationsEntryName = "software-configurations.json";
    private const long MaximumCatalogBytes = 10 * 1024 * 1024;
    private const long MaximumConfigurationsBytes = 25 * 1024 * 1024;

    private readonly HttpClient _httpClient;
    private readonly ILogger<IntelGraphicsSource> _logger;

    public IntelGraphicsSource(HttpClient httpClient, ILogger<IntelGraphicsSource> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(logger);
        _httpClient = httpClient;
        _logger = logger;
    }

    public UpdateSource Kind => UpdateSource.Oem;

    public string DisplayName => "Intel Graphics";

    public async IAsyncEnumerable<UpdateCandidate> SearchAsync(
        IReadOnlyCollection<DriverInfo> drivers,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(drivers);

        var intelDisplays = drivers
            .Where(IsSupportedIntelDisplayDriver)
            .GroupBy(driver => driver.DeviceId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();

        _logger.LogInformation("Intel DSA source matched {Count} display adapters", intelDisplays.Length);
        if (intelDisplays.Length == 0)
        {
            yield break;
        }

        string configurations;
        try
        {
            using var response = await _httpClient.GetAsync(
                CatalogPath,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            if (response.Content.Headers.ContentLength is > MaximumCatalogBytes)
            {
                _logger.LogWarning("Intel DSA catalog is unexpectedly large: {Bytes} bytes", response.Content.Headers.ContentLength);
                yield break;
            }

            var archiveBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (!TryExtractConfigurations(archiveBytes, out configurations, out var extractionError))
            {
                _logger.LogWarning("Intel DSA catalog could not be read: {Error}", extractionError);
                yield break;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Intel DSA catalog request failed");
            yield break;
        }

        var target = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)
            ? IntelWindowsTarget.Windows11
            : IntelWindowsTarget.Windows10;

        foreach (var driver in intelDisplays)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryFindLatestRelease(configurations, driver, target, out var release))
            {
                _logger.LogInformation(
                    "Intel DSA catalog has no compatible graphics package for {Device} ({HardwareId})",
                    driver.DeviceName,
                    driver.HardwareId);
                continue;
            }

            var candidate = BuildCandidate(driver, release);
            if (!candidate.IsNewerThan(driver))
            {
                _logger.LogInformation(
                    "Intel graphics driver for {Device} is current: installed {InstalledVersion} ({InstalledDate}), catalog {CatalogVersion} ({CatalogDate})",
                    driver.DeviceName,
                    driver.CurrentVersion,
                    driver.CurrentDate,
                    release.Version,
                    release.ReleaseDate);
                continue;
            }

            _logger.LogInformation(
                "Intel DSA matched {Device} to package {Id}, version {Version}, {Date}, {Url}",
                driver.DeviceName,
                release.Id,
                release.Version,
                release.ReleaseDate,
                release.DownloadUrl);
            yield return candidate;
        }
    }

    internal static bool TryExtractConfigurations(
        byte[] archiveBytes,
        out string configurations,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(archiveBytes);
        configurations = string.Empty;
        error = string.Empty;

        if (archiveBytes.LongLength == 0 || archiveBytes.LongLength > MaximumCatalogBytes)
        {
            error = "The catalog archive is empty or exceeds the size limit.";
            return false;
        }

        try
        {
            using var stream = new MemoryStream(archiveBytes, writable: false);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            var entry = archive.GetEntry(ConfigurationsEntryName);
            if (entry is null)
            {
                error = $"The archive does not contain {ConfigurationsEntryName}.";
                return false;
            }
            if (entry.Length <= 0 || entry.Length > MaximumConfigurationsBytes)
            {
                error = "The software configuration entry is empty or exceeds the size limit.";
                return false;
            }

            using var reader = new StreamReader(entry.Open());
            configurations = reader.ReadToEnd();
            return !string.IsNullOrWhiteSpace(configurations);
        }
        catch (InvalidDataException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    internal static bool TryFindLatestRelease(
        string configurationsJson,
        DriverInfo driver,
        IntelWindowsTarget target,
        out IntelGraphicsRelease release)
    {
        ArgumentNullException.ThrowIfNull(configurationsJson);
        ArgumentNullException.ThrowIfNull(driver);
        release = default;

        if (!TryExtractIntelDeviceId(driver, out var deviceId))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(configurationsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            IntelGraphicsRelease? best = null;
            foreach (var entry in document.RootElement.EnumerateArray())
            {
                if (!EntrySupportsGraphicsDevice(entry, deviceId)
                    || entry.TryGetProperty("IsBeta", out var beta) && beta.ValueKind == JsonValueKind.True
                    || !TryParseEntry(entry, target, out var parsed))
                {
                    continue;
                }

                if (best is null
                    || parsed.ReleaseDate > best.Value.ReleaseDate
                    || parsed.ReleaseDate == best.Value.ReleaseDate && parsed.Version > best.Value.Version)
                {
                    best = parsed;
                }
            }

            if (best is null)
            {
                return false;
            }

            release = best.Value;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static UpdateCandidate BuildCandidate(DriverInfo driver, IntelGraphicsRelease release) =>
        new(
            ForHardwareId: driver.HardwareId,
            Source: UpdateSource.Oem,
            NewVersion: release.Version,
            NewDate: release.ReleaseDate,
            DownloadUrl: release.DownloadUrl,
            SizeBytes: release.SizeBytes,
            KbArticle: null,
            IsSuperseded: false,
            SourceUpdateId: $"vendor-installer:intel-graphics:{release.Id}:{release.Version}",
            SupersededIds: Array.Empty<string>(),
            InstallKind: UpdateInstallKind.VendorInstaller,
            Confidence: UpdateConfidence.Confirmed);

    internal static bool IsSupportedIntelDisplayDriver(DriverInfo driver) =>
        driver.Category == DriverCategory.Display
        && (Contains(driver.Provider, "Intel")
            || Contains(driver.Manufacturer, "Intel")
            || Contains(driver.DeviceName, "Intel"))
        && TryExtractIntelDeviceId(driver, out _);

    internal static bool TryExtractIntelDeviceId(DriverInfo driver, out ushort deviceId)
    {
        foreach (var hardwareId in driver.HardwareIds
                     .Append(driver.HardwareId)
                     .Append(driver.DeviceId))
        {
            var match = IntelDeviceIdPattern().Match(hardwareId ?? string.Empty);
            if (match.Success
                && ushort.TryParse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out deviceId))
            {
                return true;
            }
        }

        deviceId = 0;
        return false;
    }

    private static bool EntrySupportsGraphicsDevice(JsonElement entry, ushort deviceId)
    {
        if (!entry.TryGetProperty("Components", out var components)
            || components.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var needle = $"VEN_8086&DEV_{deviceId:X4}";
        foreach (var component in components.EnumerateArray())
        {
            if (!component.TryGetProperty("Category", out var category)
                || !string.Equals(category.GetString(), "Graphics", StringComparison.OrdinalIgnoreCase)
                || !component.TryGetProperty("DetectionValues", out var values)
                || values.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var value in values.EnumerateArray())
            {
                var detection = value.GetString();
                if (detection is not null
                    && detection.StartsWith(needle, StringComparison.OrdinalIgnoreCase)
                    && (detection.Length == needle.Length || detection[needle.Length] == '&'))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryParseEntry(
        JsonElement entry,
        IntelWindowsTarget target,
        out IntelGraphicsRelease release)
    {
        release = default;
        var id = entry.TryGetProperty("Id", out var idElement) && idElement.TryGetInt64(out var parsedId)
            ? parsedId
            : 0;
        var rawVersion = entry.TryGetProperty("Version", out var versionElement)
            ? versionElement.GetString()
            : null;
        var versionToken = rawVersion?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        var rawDate = entry.TryGetProperty("DisplayReleaseDate", out var dateElement)
            ? dateElement.GetString()
            : null;

        if (id <= 0
            || !Version.TryParse(versionToken, out var version)
            || !TryParseDate(rawDate, out var releaseDate)
            || !TryChooseFile(entry, target, out var downloadUrl, out var sizeBytes))
        {
            return false;
        }

        release = new IntelGraphicsRelease(id, version, releaseDate, downloadUrl, sizeBytes);
        return true;
    }

    private static bool TryChooseFile(
        JsonElement entry,
        IntelWindowsTarget target,
        out Uri downloadUrl,
        out long sizeBytes)
    {
        downloadUrl = null!;
        sizeBytes = 0;
        if (!entry.TryGetProperty("Files", out var files) || files.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var osPrefix = target == IntelWindowsTarget.Windows11 ? "windows-11-" : "windows-10-";
        foreach (var file in files.EnumerateArray())
        {
            var rawUrl = file.TryGetProperty("Url", out var urlElement) ? urlElement.GetString() : null;
            if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var candidateUrl)
                || candidateUrl.Scheme != Uri.UriSchemeHttps
                || !candidateUrl.Host.Equals("downloadmirror.intel.com", StringComparison.OrdinalIgnoreCase)
                || !Path.GetExtension(candidateUrl.LocalPath).Equals(".exe", StringComparison.OrdinalIgnoreCase)
                || !FileSupportsTarget(file, osPrefix))
            {
                continue;
            }

            downloadUrl = candidateUrl;
            sizeBytes = file.TryGetProperty("Size", out var sizeElement) && sizeElement.TryGetInt64(out var size)
                ? Math.Max(0, size)
                : 0;
            return true;
        }

        return false;
    }

    private static bool FileSupportsTarget(JsonElement file, string osPrefix)
    {
        if (!file.TryGetProperty("OperatingSystems", out var operatingSystems)
            || operatingSystems.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        return operatingSystems.EnumerateArray()
            .Select(value => value.GetString())
            .Any(value => value is not null
                && value.StartsWith(osPrefix, StringComparison.OrdinalIgnoreCase)
                && value.EndsWith("-64", StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryParseDate(string? raw, out DateOnly date)
    {
        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp))
        {
            date = DateOnly.FromDateTime(timestamp.UtcDateTime);
            return true;
        }

        return DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    private static bool Contains(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"VEN_8086&DEV_([0-9A-F]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex IntelDeviceIdPattern();

    internal enum IntelWindowsTarget
    {
        Windows10,
        Windows11
    }

    internal readonly record struct IntelGraphicsRelease(
        long Id,
        Version Version,
        DateOnly ReleaseDate,
        Uri DownloadUrl,
        long SizeBytes);
}
