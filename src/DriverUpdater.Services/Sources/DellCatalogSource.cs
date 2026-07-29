using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using DriverUpdater.Services.Sources.Internal.Dell;
using Microsoft.Extensions.Logging;

namespace DriverUpdater.Services.Sources;

// Official Dell driver catalog (CatalogPC.cab): exact download URLs, versions, and
// PCI + platform applicability straight from Dell, with no HTML scraping and no
// anti-bot walls. Every DUP it lists installs silently with /s.
public sealed partial class DellCatalogSource : IUpdateSource
{
    private const string CimV2Scope = "\\\\.\\root\\CIMV2";
    private const string SystemSkuQuery = "SELECT SystemSKUNumber FROM Win32_ComputerSystem";

    private readonly IOemDetectionService _oemDetectionService;
    private readonly IDellCatalogProvider _catalogProvider;
    private readonly IWmiQueryRunner _wmi;
    private readonly ILogger<DellCatalogSource> _logger;

    public DellCatalogSource(
        IOemDetectionService oemDetectionService,
        IDellCatalogProvider catalogProvider,
        IWmiQueryRunner wmi,
        ILogger<DellCatalogSource> logger)
    {
        ArgumentNullException.ThrowIfNull(oemDetectionService);
        ArgumentNullException.ThrowIfNull(catalogProvider);
        ArgumentNullException.ThrowIfNull(wmi);
        ArgumentNullException.ThrowIfNull(logger);
        _oemDetectionService = oemDetectionService;
        _catalogProvider = catalogProvider;
        _wmi = wmi;
        _logger = logger;
    }

    public UpdateSource Kind => UpdateSource.Oem;

    public string DisplayName => "Dell driver catalog";

    public async IAsyncEnumerable<UpdateCandidate> SearchAsync(
        IReadOnlyCollection<DriverInfo> drivers,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(drivers);

        var oem = await _oemDetectionService.DetectAsync(cancellationToken).ConfigureAwait(false);
        if (oem is null || oem.Vendor != OemVendor.Dell)
        {
            yield break;
        }

        var systemId = await GetSystemIdAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(systemId))
        {
            _logger.LogInformation("Dell catalog source skipped: SystemSKUNumber unavailable via WMI");
            yield break;
        }

        var xmlPath = await _catalogProvider.GetCatalogXmlPathAsync(cancellationToken).ConfigureAwait(false);
        if (xmlPath is null)
        {
            yield break;
        }

        List<DellCatalogEntry> applicable;
        try
        {
            using var reader = XmlReader.Create(xmlPath, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
            applicable = DellCatalogParser.ParseDriverComponents(reader)
                .Where(entry => entry.AppliesToSystem(systemId))
                .ToList();
        }
        catch (Exception ex) when (ex is XmlException or IOException)
        {
            _logger.LogWarning(ex, "Dell catalog parse failed for {Path}", xmlPath);
            yield break;
        }

        _logger.LogInformation(
            "Dell catalog: {Count} driver packages apply to system ID {SystemId}",
            applicable.Count, systemId);

        var offeredReleases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var driver in drivers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryExtractPciIds(driver.HardwareId, out var vendorId, out var deviceId))
            {
                continue;
            }

            var entry = applicable
                .Where(candidate => candidate.MatchesPciDevice(vendorId, deviceId))
                .Where(candidate => candidate.VendorVersion is not null)
                .OrderByDescending(candidate => candidate.VendorVersion)
                .FirstOrDefault();
            if (entry is null || !offeredReleases.Add(entry.ReleaseId))
            {
                continue;
            }

            if (driver.CurrentVersion is null || entry.VendorVersion! <= driver.CurrentVersion)
            {
                continue;
            }

            _logger.LogInformation(
                "Dell catalog: {Package} {Version} applies to {Device} (current {Current})",
                entry.Name, entry.VendorVersion, driver.DeviceName, driver.CurrentVersion);
            yield return new UpdateCandidate(
                ForHardwareId: driver.HardwareId,
                Source: UpdateSource.Oem,
                NewVersion: entry.VendorVersion!,
                NewDate: entry.ReleaseDate ?? DateOnly.MinValue,
                DownloadUrl: new Uri($"https://downloads.dell.com/{entry.PackagePath}"),
                SizeBytes: entry.SizeBytes,
                KbArticle: null,
                IsSuperseded: false,
                SourceUpdateId: $"vendor-installer:dell-dup:{entry.ReleaseId}",
                SupersededIds: Array.Empty<string>(),
                InstallKind: UpdateInstallKind.VendorInstaller,
                Confidence: UpdateConfidence.Confirmed);
        }
    }

    private async Task<string?> GetSystemIdAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var row in _wmi.QueryAsync(CimV2Scope, SystemSkuQuery, cancellationToken).ConfigureAwait(false))
            {
                return row.TryGetValue("SystemSKUNumber", out var sku) ? sku?.ToString()?.Trim() : null;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Dell catalog source: WMI SystemSKUNumber query failed");
        }
        return null;
    }

    internal static bool TryExtractPciIds(string? hardwareId, out string vendorId, out string deviceId)
    {
        vendorId = string.Empty;
        deviceId = string.Empty;
        if (string.IsNullOrWhiteSpace(hardwareId))
        {
            return false;
        }

        var match = PciIdPattern().Match(hardwareId);
        if (!match.Success)
        {
            return false;
        }

        vendorId = match.Groups["ven"].Value;
        deviceId = match.Groups["dev"].Value;
        return true;
    }

    [GeneratedRegex(@"PCI\\VEN_(?<ven>[0-9A-F]{4})&DEV_(?<dev>[0-9A-F]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex PciIdPattern();
}
