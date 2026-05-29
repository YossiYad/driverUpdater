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
    private readonly JsonSerializerOptions _serializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = null
    };

    public JsonDriverCacheStore(ILogger<JsonDriverCacheStore> logger, string? overridePath = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        CachePath = overridePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            DefaultFolderName,
            CacheFileName);
    }

    public string CachePath { get; }

    public async Task<DriverCacheSnapshot?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(CachePath))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(CachePath);
            return await JsonSerializer.DeserializeAsync<DriverCacheSnapshot>(stream, _serializerOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not parse {Path}; ignoring the driver cache", CachePath);
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
