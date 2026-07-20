using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using DriverUpdater.Core.Options;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DriverUpdater.Services.Sources;

public sealed partial class MicrosoftCatalogSource : IUpdateSource
{
    private static readonly IReadOnlyDictionary<string, CatalogDownloadInfo> EmptyDownloadMap =
        new Dictionary<string, CatalogDownloadInfo>(StringComparer.OrdinalIgnoreCase);

    private readonly ICatalogHttpClient _httpClient;
    private readonly IMemoryCache _cache;
    private readonly IOptionsMonitor<CatalogSettings> _settings;
    private readonly ILogger<MicrosoftCatalogSource> _logger;

    public MicrosoftCatalogSource(
        ICatalogHttpClient httpClient,
        IMemoryCache cache,
        IOptionsMonitor<CatalogSettings> settings,
        ILogger<MicrosoftCatalogSource> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);
        _httpClient = httpClient;
        _cache = cache;
        _settings = settings;
        _logger = logger;
    }

    public UpdateSource Kind => UpdateSource.MicrosoftCatalog;

    public string DisplayName => "Microsoft Update Catalog";

    public async IAsyncEnumerable<UpdateCandidate> SearchAsync(
        IReadOnlyCollection<DriverInfo> drivers,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(drivers);

        var settings = _settings.CurrentValue;
        if (!settings.Enabled)
        {
            _logger.LogDebug("Microsoft Update Catalog source is disabled");
            yield break;
        }

        var driversByHardwareId = new Dictionary<string, List<DriverInfo>>(StringComparer.OrdinalIgnoreCase);
        foreach (var driver in drivers)
        {
            foreach (var hardwareId in BuildCatalogQueries(driver))
            {
                if (!driversByHardwareId.TryGetValue(hardwareId, out var matchingDrivers))
                {
                    matchingDrivers = new List<DriverInfo>();
                    driversByHardwareId[hardwareId] = matchingDrivers;
                }
                matchingDrivers.Add(driver);
            }
        }

        if (driversByHardwareId.Count == 0)
        {
            _logger.LogDebug("Catalog search skipped: no hardware IDs to query");
            yield break;
        }

        _logger.LogInformation("Catalog search starting for {Count} hardware IDs", driversByHardwareId.Count);

        var channel = Channel.CreateUnbounded<UpdateCandidate>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        var maxConcurrent = Math.Max(1, settings.MaxConcurrentSearches);
        using var throttle = new SemaphoreSlim(maxConcurrent, maxConcurrent);

        var producers = Task.Run(async () =>
        {
            try
            {
                var tasks = driversByHardwareId.Select(pair =>
                    SearchSingleHardwareIdAsync(
                        pair.Key,
                        pair.Value,
                        channel.Writer,
                        throttle,
                        cancellationToken)).ToArray();
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        }, cancellationToken);

        await foreach (var candidate in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return candidate;
        }

        await producers.ConfigureAwait(false);
    }

    internal static bool IsPnPHardwareId(string? hardwareId)
    {
        if (string.IsNullOrWhiteSpace(hardwareId))
        {
            return false;
        }

        var separator = hardwareId.IndexOf('\\');
        return separator > 0 && separator < hardwareId.Length - 1;
    }

    internal static bool IsCatalogEligibleHardwareId(string? hardwareId)
    {
        if (!IsPnPHardwareId(hardwareId))
        {
            return false;
        }

        var separator = hardwareId!.IndexOf('\\');
        var prefix = hardwareId[..separator];
        return prefix.Equals("PCI", StringComparison.OrdinalIgnoreCase)
            || prefix.Equals("USB", StringComparison.OrdinalIgnoreCase)
            || prefix.Equals("HDAUDIO", StringComparison.OrdinalIgnoreCase)
            || (prefix.Equals("ACPI", StringComparison.OrdinalIgnoreCase)
                && IsVendorSpecificAcpiId(hardwareId))
            || (prefix.Equals("HID", StringComparison.OrdinalIgnoreCase)
                && ContainsHardwareToken(hardwareId, "VID_")
                && ContainsHardwareToken(hardwareId, "PID_"))
            || (prefix.Equals("BTHENUM", StringComparison.OrdinalIgnoreCase)
                && ContainsHardwareToken(hardwareId, "DEV_"));
    }

    private static bool ContainsHardwareToken(string value, string token) =>
        value.Contains(token, StringComparison.OrdinalIgnoreCase);

    private static bool IsVendorSpecificAcpiId(string hardwareId)
    {
        var match = AcpiVendorHardwareIdPattern().Match(hardwareId);
        if (!match.Success)
        {
            return false;
        }

        var vendor = match.Groups["ven"].Value;
        return !vendor.Equals("PNP", StringComparison.OrdinalIgnoreCase)
            && !vendor.Equals("ACPI", StringComparison.OrdinalIgnoreCase)
            && !vendor.Equals("MSFT", StringComparison.OrdinalIgnoreCase);
    }

    internal static IReadOnlyList<string> BuildCatalogQueries(DriverInfo driver)
    {
        var queries = new List<string>();
        AddExpandedIfEligible(queries, driver.HardwareId);

        var primaryPci = PciHardwareIdPattern().Match(driver.HardwareId);
        if (primaryPci.Success && !SubsystemPattern().IsMatch(driver.HardwareId))
        {
            var specificPciId = driver.HardwareIds.FirstOrDefault(id =>
            {
                var alternate = PciHardwareIdPattern().Match(id);
                return alternate.Success
                    && SubsystemPattern().IsMatch(id)
                    && alternate.Groups["ven"].Value.Equals(primaryPci.Groups["ven"].Value, StringComparison.OrdinalIgnoreCase)
                    && alternate.Groups["dev"].Value.Equals(primaryPci.Groups["dev"].Value, StringComparison.OrdinalIgnoreCase);
            });
            AddExpandedIfEligible(queries, specificPciId);
        }

        if (driver.HardwareId.StartsWith("ACPI\\", StringComparison.OrdinalIgnoreCase)
            && !IsCatalogEligibleHardwareId(driver.HardwareId))
        {
            AddExpandedIfEligible(
                queries,
                driver.HardwareIds.FirstOrDefault(id =>
                    id.StartsWith("ACPI\\", StringComparison.OrdinalIgnoreCase)
                    && IsCatalogEligibleHardwareId(id)));
        }

        return queries.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void AddExpandedIfEligible(List<string> queries, string? hardwareId)
    {
        if (!IsCatalogEligibleHardwareId(hardwareId))
        {
            return;
        }

        foreach (var query in ExpandHardwareIdQueries(hardwareId!))
        {
            AddIfMissing(queries, query);
        }
    }

    private async Task SearchSingleHardwareIdAsync(
        string hardwareId,
        IReadOnlyCollection<DriverInfo> matchingDrivers,
        ChannelWriter<UpdateCandidate> writer,
        SemaphoreSlim throttle,
        CancellationToken cancellationToken)
    {
        await throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hits = await GetOrFetchHitsAsync(hardwareId, cancellationToken).ConfigureAwait(false);

            if (hits.Count == 0)
            {
                _logger.LogDebug("Catalog search for {HardwareId} returned no hits", hardwareId);
                return;
            }

            var potentiallyNewerHits = hits
                .Where(hit => IsPotentiallyNewer(hit, hardwareId, matchingDrivers))
                .ToArray();
            if (potentiallyNewerHits.Length == 0)
            {
                _logger.LogDebug(
                    "Catalog search for {HardwareId} returned {HitCount} hit(s), but none are newer than the installed driver",
                    hardwareId,
                    hits.Count);
                return;
            }

            var ids = potentiallyNewerHits
                .Select(h => h.UpdateId)
                .ToArray();
            IReadOnlyList<CatalogDownloadInfo> downloads = Array.Empty<CatalogDownloadInfo>();
            try
            {
                downloads = await _httpClient.GetDownloadsAsync(ids, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Catalog download dialog failed for {HardwareId}", hardwareId);
            }

            var downloadMap = downloads.ToDictionary(d => d.UpdateId, StringComparer.OrdinalIgnoreCase);

            foreach (var hit in potentiallyNewerHits)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (TryMap(hit, hardwareId, downloadMap, out var candidate))
                {
                    if (candidate.InstallKind == UpdateInstallKind.VendorPage)
                    {
                        _logger.LogDebug(
                            "Catalog hit {UpdateId} for {HardwareId} has no downloadable package; offering the catalog page instead",
                            hit.UpdateId, hardwareId);
                    }
                    await writer.WriteAsync(candidate, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    _logger.LogDebug(
                        "Catalog hit for {HardwareId} discarded: missing update id (title={Title})",
                        hardwareId, hit.Title);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Catalog search failed for {HardwareId}", hardwareId);
        }
        finally
        {
            throttle.Release();
        }
    }

    private static bool IsPotentiallyNewer(
        CatalogSearchHit hit,
        string hardwareId,
        IReadOnlyCollection<DriverInfo> matchingDrivers)
    {
        if (!TryMap(
                hit,
                hardwareId,
                EmptyDownloadMap,
                out var candidate))
        {
            return false;
        }

        return matchingDrivers.Any(candidate.IsNewerThan);
    }

    private async Task<IReadOnlyList<CatalogSearchHit>> GetOrFetchHitsAsync(string hardwareId, CancellationToken cancellationToken)
    {
        var cacheKey = $"catalog:hits:{hardwareId}";
        if (_cache.TryGetValue<IReadOnlyList<CatalogSearchHit>>(cacheKey, out var cached) && cached is not null)
        {
            return cached;
        }

        var hits = await _httpClient.SearchAsync(hardwareId, cancellationToken).ConfigureAwait(false);
        _cache.Set(cacheKey, hits, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = _settings.CurrentValue.CacheDuration
        });
        return hits;
    }

    internal static IReadOnlyList<string> ExpandHardwareIdQueries(string hardwareId)
    {
        if (string.IsNullOrWhiteSpace(hardwareId))
        {
            return Array.Empty<string>();
        }

        var queries = new List<string> { hardwareId };
        var pci = PciHardwareIdPattern().Match(hardwareId);
        if (pci.Success)
        {
            var venDev = $"{pci.Groups["prefix"].Value}\\VEN_{pci.Groups["ven"].Value}&DEV_{pci.Groups["dev"].Value}";
            AddIfMissing(queries, venDev);

            var subsys = SubsystemPattern().Match(hardwareId);
            if (subsys.Success)
            {
                AddIfMissing(queries, $"{venDev}&SUBSYS_{subsys.Groups["subsys"].Value}");
            }
        }

        var usb = UsbHardwareIdPattern().Match(hardwareId);
        if (usb.Success)
        {
            AddIfMissing(queries, $"{usb.Groups["prefix"].Value}\\VID_{usb.Groups["vid"].Value}&PID_{usb.Groups["pid"].Value}");
        }

        return queries;
    }

    private static void AddIfMissing(List<string> queries, string value)
    {
        if (!queries.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            queries.Add(value);
        }
    }

    internal static bool TryMap(
        CatalogSearchHit hit,
        string hardwareId,
        IReadOnlyDictionary<string, CatalogDownloadInfo> downloadMap,
        out UpdateCandidate candidate)
    {
        if (string.IsNullOrWhiteSpace(hit.UpdateId))
        {
            candidate = null!;
            return false;
        }

        var version = hit.Version
            ?? (hit.LastUpdatedDate is { } date ? new Version(date.Year, date.Month, date.Day, 0) : new Version(0, 0, 0, 0));
        var newDate = hit.LastUpdatedDate ?? DateOnly.MinValue;

        Uri downloadUrl;
        long size = hit.SizeBytes ?? 0;
        if (downloadMap.TryGetValue(hit.UpdateId, out var info))
        {
            // The Microsoft Update Catalog occasionally returns a non-driver executable package
            // for a hardware-id query - e.g. rootsupd.exe (the Windows Root Certificates update)
            // matched against the Hyper-V virtualization device. These are not pnputil-installable
            // drivers, so discard the hit rather than offering a bogus update that can only be
            // skipped or fail. Genuine catalog driver packages ship as .cab (or .msu/.msi).
            var packageExt = Path.GetExtension(info.DownloadUrl.AbsolutePath);
            if (packageExt.Equals(".exe", StringComparison.OrdinalIgnoreCase))
            {
                candidate = null!;
                return false;
            }

            downloadUrl = info.DownloadUrl;
            if (info.SizeBytes.HasValue)
            {
                size = info.SizeBytes.Value;
            }
        }
        else
        {
            downloadUrl = new Uri($"https://www.catalog.update.microsoft.com/ScopedViewInline.aspx?updateid={hit.UpdateId}");
        }

        var hasDownload = downloadMap.ContainsKey(hit.UpdateId);
        candidate = new UpdateCandidate(
            ForHardwareId: hardwareId,
            Source: UpdateSource.MicrosoftCatalog,
            NewVersion: version,
            NewDate: newDate,
            DownloadUrl: downloadUrl,
            SizeBytes: size,
            KbArticle: null,
            IsSuperseded: false,
            SourceUpdateId: hit.UpdateId,
            SupersededIds: Array.Empty<string>(),
            InstallKind: hasDownload ? UpdateInstallKind.PnPUtilPackage : UpdateInstallKind.VendorPage,
            Confidence: hasDownload ? UpdateConfidence.Confirmed : UpdateConfidence.Advisory);
        return true;
    }

    [GeneratedRegex(@"(?<prefix>PCI)\\VEN_(?<ven>[0-9A-F]{4})&DEV_(?<dev>[0-9A-F]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex PciHardwareIdPattern();

    [GeneratedRegex(@"SUBSYS_(?<subsys>[0-9A-F]{8})", RegexOptions.IgnoreCase)]
    private static partial Regex SubsystemPattern();

    [GeneratedRegex(@"(?<prefix>USB)\\VID_(?<vid>[0-9A-F]{4})&PID_(?<pid>[0-9A-F]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex UsbHardwareIdPattern();

    [GeneratedRegex(@"^ACPI\\VEN_(?<ven>[0-9A-Z]{3,4})&DEV_[0-9A-Z]{4}", RegexOptions.IgnoreCase)]
    private static partial Regex AcpiVendorHardwareIdPattern();
}
