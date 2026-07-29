using System.Runtime.CompilerServices;
using System.Xml;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using DriverUpdater.Services.Sources.Internal.Hp;
using Microsoft.Extensions.Logging;

namespace DriverUpdater.Services.Sources;

// HP's official HPIA reference files: per-platform softpaq lists with exact download
// URLs and versions, fetched from hpia.hpcloud.hp.com with no scraping. Softpaqs are
// matched to local drivers by category plus component-vendor tokens, so the matches
// are offered as Advisory rather than Confirmed.
public sealed class HpSoftpaqSource : IUpdateSource
{
    private const string CimV2Scope = "\\\\.\\root\\CIMV2";
    private const string BaseBoardQuery = "SELECT Product FROM Win32_BaseBoard";
    private const string OsVersionQuery = "SELECT Version FROM Win32_OperatingSystem";

    private static readonly string[] ComponentVendorTokens =
    [
        "Realtek", "Intel", "NVIDIA", "AMD", "Qualcomm", "MediaTek", "Broadcom",
        "Synaptics", "ELAN", "Alps", "Conexant", "Waves", "Sunplus", "Genesys"
    ];

    private readonly IOemDetectionService _oemDetectionService;
    private readonly IHpSoftpaqRefProvider _refProvider;
    private readonly IWmiQueryRunner _wmi;
    private readonly ILogger<HpSoftpaqSource> _logger;

    public HpSoftpaqSource(
        IOemDetectionService oemDetectionService,
        IHpSoftpaqRefProvider refProvider,
        IWmiQueryRunner wmi,
        ILogger<HpSoftpaqSource> logger)
    {
        ArgumentNullException.ThrowIfNull(oemDetectionService);
        ArgumentNullException.ThrowIfNull(refProvider);
        ArgumentNullException.ThrowIfNull(wmi);
        ArgumentNullException.ThrowIfNull(logger);
        _oemDetectionService = oemDetectionService;
        _refProvider = refProvider;
        _wmi = wmi;
        _logger = logger;
    }

    public UpdateSource Kind => UpdateSource.Oem;

    public string DisplayName => "HP softpaq reference";

    public async IAsyncEnumerable<UpdateCandidate> SearchAsync(
        IReadOnlyCollection<DriverInfo> drivers,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(drivers);

        var oem = await _oemDetectionService.DetectAsync(cancellationToken).ConfigureAwait(false);
        if (oem is null || oem.Vendor != OemVendor.Hp)
        {
            yield break;
        }

        var platformId = await QuerySingleValueAsync(BaseBoardQuery, "Product", cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(platformId))
        {
            _logger.LogInformation("HP softpaq source skipped: baseboard product (platform ID) unavailable");
            yield break;
        }

        var osVersion = await QuerySingleValueAsync(OsVersionQuery, "Version", cancellationToken).ConfigureAwait(false);
        var osToken = BuildOsToken(osVersion);
        if (osToken is null)
        {
            _logger.LogInformation("HP softpaq source skipped: Windows version {Version} has no known reference token", osVersion);
            yield break;
        }

        var xmlPath = await _refProvider.GetReferenceXmlPathAsync(platformId.Trim(), osToken, cancellationToken).ConfigureAwait(false);
        if (xmlPath is null)
        {
            yield break;
        }

        List<HpSoftpaqEntry> softpaqs;
        try
        {
            using var reader = XmlReader.Create(xmlPath, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
            softpaqs = HpSoftpaqParser.ParseSolutions(reader)
                .Where(entry => entry.IsDriver && entry.Version is not null)
                .ToList();
        }
        catch (Exception ex) when (ex is XmlException or IOException)
        {
            _logger.LogWarning(ex, "HP softpaq reference parse failed for {Path}", xmlPath);
            yield break;
        }

        _logger.LogInformation(
            "HP softpaq reference lists {Count} driver softpaqs for platform {Platform} / {Os}",
            softpaqs.Count, platformId, osToken);

        var offered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var driver in drivers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (driver.CurrentVersion is null)
            {
                continue;
            }

            var entry = softpaqs
                .Where(candidate => Matches(candidate, driver))
                .OrderByDescending(candidate => candidate.Version)
                .FirstOrDefault();
            if (entry is null
                || entry.Version! <= driver.CurrentVersion
                || !offered.Add(entry.Id))
            {
                continue;
            }

            _logger.LogInformation(
                "HP softpaq {Id} ({Name} {Version}) matches {Device} (current {Current})",
                entry.Id, entry.Name, entry.Version, driver.DeviceName, driver.CurrentVersion);
            yield return new UpdateCandidate(
                ForHardwareId: driver.HardwareId,
                Source: UpdateSource.Oem,
                NewVersion: entry.Version!,
                NewDate: entry.ReleaseDate ?? DateOnly.MinValue,
                DownloadUrl: entry.DownloadUrl,
                SizeBytes: entry.SizeBytes,
                KbArticle: null,
                IsSuperseded: false,
                SourceUpdateId: $"vendor-installer:hp-softpaq:{entry.Id}",
                SupersededIds: Array.Empty<string>(),
                InstallKind: UpdateInstallKind.VendorInstaller,
                Confidence: UpdateConfidence.Advisory);
        }
    }

    internal static bool Matches(HpSoftpaqEntry entry, DriverInfo driver)
    {
        if (!CategoryMatches(entry.Category, driver.Category))
        {
            return false;
        }

        var driverText = $"{driver.Provider} {driver.Manufacturer} {driver.DeviceName}";
        return ComponentVendorTokens.Any(token =>
            entry.Name.Contains(token, StringComparison.OrdinalIgnoreCase)
            && driverText.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    internal static bool CategoryMatches(string softpaqCategory, DriverCategory driverCategory)
    {
        var category = softpaqCategory.ToLowerInvariant();
        return driverCategory switch
        {
            DriverCategory.Audio => category.Contains("audio"),
            DriverCategory.Display => category.Contains("graphics") || category.Contains("video"),
            DriverCategory.Network => category.Contains("network") || category.Contains("lan") || category.Contains("wireless"),
            DriverCategory.Bluetooth => category.Contains("bluetooth") || category.Contains("wireless"),
            DriverCategory.Chipset => category.Contains("chipset"),
            DriverCategory.Storage => category.Contains("storage"),
            DriverCategory.Usb => category.Contains("usb") || category.Contains("dock"),
            DriverCategory.Security => category.Contains("security"),
            _ => false
        };
    }

    // HPIA reference names use the marketing OS release, e.g. 11.0.24H2, not the build
    // number, so the WMI build is translated through this table.
    internal static string? BuildOsToken(string? osVersion)
    {
        if (string.IsNullOrWhiteSpace(osVersion))
        {
            return null;
        }

        var parts = osVersion.Split('.');
        if (parts.Length < 3 || !int.TryParse(parts[2], out var build))
        {
            return null;
        }

        return build switch
        {
            >= 26200 => "11.0.25H2",
            >= 26100 => "11.0.24H2",
            >= 22631 => "11.0.23H2",
            >= 22621 => "11.0.22H2",
            >= 22000 => "11.0.21H2",
            >= 19045 => "10.0.22H2",
            >= 19044 => "10.0.21H2",
            _ => null
        };
    }

    private async Task<string?> QuerySingleValueAsync(string query, string column, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var row in _wmi.QueryAsync(CimV2Scope, query, cancellationToken).ConfigureAwait(false))
            {
                return row.TryGetValue(column, out var value) ? value?.ToString()?.Trim() : null;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "HP softpaq source: WMI query failed ({Query})", query);
        }
        return null;
    }
}
