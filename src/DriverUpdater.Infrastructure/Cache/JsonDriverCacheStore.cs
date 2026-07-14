using System.Text.Json;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using Microsoft.Extensions.Logging;

namespace DriverUpdater.Infrastructure.Cache;

public sealed class JsonDriverCacheStore : IDriverCacheStore
{
    public const string DefaultFolderName = "DriverUpdater";
    public const string CacheFileName = "driver-cache.json";

    private readonly ILogger<JsonDriverCacheStore> _logger;
    private readonly string? _legacyCachePath;
    private readonly JsonSerializerOptions _serializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = null
    };

    public JsonDriverCacheStore(ILogger<JsonDriverCacheStore> logger, string? overridePath = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        if (overridePath is not null)
        {
            CachePath = overridePath;
            return;
        }

        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            DefaultFolderName);
        CachePath = Path.Combine(folder, BuildMachineCacheFileName(Environment.MachineName));
        _legacyCachePath = Path.Combine(folder, CacheFileName);
    }

    public string CachePath { get; }

    // Each machine gets its own cache file so a shared/synced ProgramData or a copied
    // disk image never mixes one PC's driver inventory into another's.
    internal static string BuildMachineCacheFileName(string machineName)
    {
        var safe = string.Join("_", machineName.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (string.IsNullOrWhiteSpace(safe))
        {
            safe = "default";
        }
        return $"driver-cache.{safe}.json";
    }

    public async Task<DriverCacheSnapshot?> LoadAsync(CancellationToken cancellationToken = default)
    {
        var path = CachePath;
        if (!File.Exists(path))
        {
            if (_legacyCachePath is not null && File.Exists(_legacyCachePath))
            {
                _logger.LogInformation(
                    "No per-machine driver cache at {Path}; migrating from legacy cache {Legacy}",
                    path, _legacyCachePath);
                path = _legacyCachePath;
            }
            else
            {
                _logger.LogInformation("No driver cache at {Path}; first run or cache was cleared", path);
                return null;
            }
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var snapshot = await JsonSerializer.DeserializeAsync<DriverCacheSnapshot>(stream, _serializerOptions, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Loaded driver cache from {Path}: {Count} entries, captured at {CapturedAt}",
                path, snapshot?.Entries.Count ?? 0, snapshot?.CapturedAt);
            return snapshot;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not parse {Path}; ignoring the driver cache", path);
            return null;
        }
    }

    public async Task SaveAsync(DriverCacheSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var directory = Path.GetDirectoryName(CachePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = CachePath + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, snapshot, _serializerOptions, cancellationToken).ConfigureAwait(false);
        }
        File.Move(tempPath, CachePath, overwrite: true);

        _logger.LogInformation("Saved {Count} drivers to cache at {Path}", snapshot.Entries.Count, CachePath);
    }
}
