using System.Runtime.CompilerServices;
using System.Threading.Channels;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using DriverUpdater.Core.Options;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DriverUpdater.Services.Sources;

public sealed class MicrosoftCatalogSource : IUpdateSource
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
            .Select(d => d.HardwareId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (hardwareIds.Length == 0)
        {
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
                    await writer.WriteAsync(candidate, cancellationToken).ConfigureAwait(false);
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
            SupersededIds: Array.Empty<string>());
        return true;
    }
}
