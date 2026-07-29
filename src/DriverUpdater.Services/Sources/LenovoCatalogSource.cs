using System.Runtime.CompilerServices;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using DriverUpdater.Services.Sources.Internal.Lenovo;
using Microsoft.Extensions.Logging;

namespace DriverUpdater.Services.Sources;

// Lenovo's official per-machine catalog (download.lenovo.com/catalog/{MTM}_{OS}.xml):
// the same data Thin Installer consumes. Each package descriptor carries the driver
// version, installer URL, and the PnP IDs it applies to, so matches are exact and no
// HTML is scraped.
public sealed class LenovoCatalogSource : IUpdateSource
{
    public const string HttpClientName = "LenovoCatalog";

    private const int MaxDescriptorFetches = 80;

    private static readonly string[] DriverCategoryTokens =
    [
        "audio", "bios", "camera", "chipset", "display", "graphics", "fingerprint",
        "networking", "wireless", "bluetooth", "storage", "usb", "dock", "power", "mouse", "pen", "touch"
    ];

    private readonly HttpClient _httpClient;
    private readonly IOemDetectionService _oemDetectionService;
    private readonly IWmiQueryRunner _wmi;
    private readonly ILogger<LenovoCatalogSource> _logger;

    public LenovoCatalogSource(
        HttpClient httpClient,
        IOemDetectionService oemDetectionService,
        IWmiQueryRunner wmi,
        ILogger<LenovoCatalogSource> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(oemDetectionService);
        ArgumentNullException.ThrowIfNull(wmi);
        ArgumentNullException.ThrowIfNull(logger);
        _httpClient = httpClient;
        _oemDetectionService = oemDetectionService;
        _wmi = wmi;
        _logger = logger;
    }

    public UpdateSource Kind => UpdateSource.Oem;

    public string DisplayName => "Lenovo driver catalog";

    public async IAsyncEnumerable<UpdateCandidate> SearchAsync(
        IReadOnlyCollection<DriverInfo> drivers,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(drivers);

        var oem = await _oemDetectionService.DetectAsync(cancellationToken).ConfigureAwait(false);
        if (oem is null || oem.Vendor != OemVendor.Lenovo)
        {
            yield break;
        }

        var machineType = ExtractMachineType(oem.Model);
        if (machineType is null)
        {
            _logger.LogInformation("Lenovo catalog source skipped: machine type unavailable from model '{Model}'", oem.Model);
            yield break;
        }

        var osToken = await GetOsTokenAsync(cancellationToken).ConfigureAwait(false);
        if (osToken is null)
        {
            yield break;
        }

        var catalogUrl = $"https://download.lenovo.com/catalog/{machineType.ToLowerInvariant()}_{osToken}.xml";
        var catalogXml = await TryGetStringAsync(catalogUrl, cancellationToken).ConfigureAwait(false);
        if (catalogXml is null)
        {
            _logger.LogInformation("Lenovo catalog not available at {Url}", catalogUrl);
            yield break;
        }

        List<LenovoPackageRef> packageRefs;
        try
        {
            packageRefs = LenovoCatalogParser.ParseCatalog(catalogXml)
                .Where(reference => IsDriverCategory(reference.Category))
                .Take(MaxDescriptorFetches)
                .ToList();
        }
        catch (System.Xml.XmlException ex)
        {
            _logger.LogWarning(ex, "Lenovo catalog parse failed for {Url}", catalogUrl);
            yield break;
        }

        _logger.LogInformation(
            "Lenovo catalog for {MachineType} ({Os}) lists {Count} driver packages",
            machineType, osToken, packageRefs.Count);

        var pciDrivers = drivers
            .Select(driver => (Driver: driver, HasPci: DellCatalogSource.TryExtractPciIds(driver.HardwareId, out var ven, out var dev), Ven: ven, Dev: dev))
            .Where(item => item.HasPci && item.Driver.CurrentVersion is not null)
            .ToList();
        if (pciDrivers.Count == 0)
        {
            yield break;
        }

        var offered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var reference in packageRefs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var descriptorXml = await TryGetStringAsync(reference.DescriptorUrl.AbsoluteUri, cancellationToken).ConfigureAwait(false);
            if (descriptorXml is null)
            {
                continue;
            }

            var package = LenovoCatalogParser.ParsePackageDescriptor(descriptorXml, reference.DescriptorUrl);
            if (package is null || package.Version is null || package.PnpIds.Count == 0)
            {
                continue;
            }

            var match = pciDrivers.FirstOrDefault(item => package.MatchesPciDevice(item.Ven, item.Dev));
            if (match.Driver is null
                || package.Version <= match.Driver.CurrentVersion
                || !offered.Add(package.Id))
            {
                continue;
            }

            _logger.LogInformation(
                "Lenovo catalog: {Package} {Version} applies to {Device} (current {Current})",
                package.Name, package.Version, match.Driver.DeviceName, match.Driver.CurrentVersion);
            yield return new UpdateCandidate(
                ForHardwareId: match.Driver.HardwareId,
                Source: UpdateSource.Oem,
                NewVersion: package.Version,
                NewDate: package.ReleaseDate ?? DateOnly.MinValue,
                DownloadUrl: package.InstallerUrl,
                SizeBytes: package.SizeBytes,
                KbArticle: null,
                IsSuperseded: false,
                SourceUpdateId: $"vendor-installer:lenovo-catalog:{package.Id}",
                SupersededIds: Array.Empty<string>(),
                InstallKind: UpdateInstallKind.VendorInstaller,
                Confidence: UpdateConfidence.Confirmed);
        }
    }

    internal static string? ExtractMachineType(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        var trimmed = model.Trim();
        if (trimmed.Length < 4)
        {
            return null;
        }

        var machineType = trimmed[..4].ToUpperInvariant();
        return machineType.All(char.IsLetterOrDigit) ? machineType : null;
    }

    internal static bool IsDriverCategory(string category)
    {
        if (string.IsNullOrWhiteSpace(category))
        {
            return false;
        }

        var lowered = category.ToLowerInvariant();
        if (lowered.Contains("bios", StringComparison.Ordinal))
        {
            return false;
        }
        return DriverCategoryTokens.Any(token => lowered.Contains(token, StringComparison.Ordinal));
    }

    private async Task<string?> GetOsTokenAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var row in _wmi.QueryAsync("\\\\.\\root\\CIMV2", "SELECT Version FROM Win32_OperatingSystem", cancellationToken).ConfigureAwait(false))
            {
                var version = row.TryGetValue("Version", out var value) ? value?.ToString() : null;
                var parts = version?.Split('.');
                if (parts is { Length: >= 3 } && int.TryParse(parts[2], out var build))
                {
                    return build >= 22000 ? "Win11" : "Win10";
                }
                return null;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Lenovo catalog source: OS version query failed");
        }
        return null;
    }

    private async Task<string?> TryGetStringAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }
            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Lenovo catalog fetch failed for {Url}", url);
            return null;
        }
    }
}
