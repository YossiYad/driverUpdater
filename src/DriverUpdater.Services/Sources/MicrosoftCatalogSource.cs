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

        var hardwareIds = drivers
            .SelectMany(d => d.HardwareIds.Count > 0 ? d.HardwareIds : new[] { d.HardwareId })
            .SelectMany(ExpandHardwareIdQueries)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (hardwareIds.Length == 0)
        {
            _logger.LogDebug("Catalog search skipped: no hardware IDs to query");
            yield break;
        }

        _logger.LogInformation("Catalog search starting for {Count} hardware IDs", hardwareIds.Length);

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
                var tasks = hardwareIds.Select(id => SearchSingleHardwareIdAsync(id, channel.Writer, throttle, cancellationToken)).ToArray();
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

    private async Task SearchSingleHardwareIdAsync(
        string hardwareId,
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

            var ids = hits.Where(h => !string.IsNullOrEmpty(h.UpdateId)).Select(h => h.UpdateId).ToArray();
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

            foreach (var hit in hits)
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
}
