using System.Text.Json;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using DriverUpdater.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DriverUpdater.Infrastructure.Cache;

public sealed class JsonDriverCacheStore : IDriverCacheStore
{
    public const string DefaultFolderName = "DriverUpdater";
    public const string CacheFileName = "driver-cache.json";

    private readonly ILogger<JsonDriverCacheStore> _logger;
    private readonly IOptionsMonitor<ScanCacheSettings>? _scanCacheSettings;
    private readonly string? _legacyCachePath;
    private static readonly TimeSpan WriteLockRetryDelay = TimeSpan.FromMilliseconds(50);
    private readonly JsonSerializerOptions _serializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = null
    };

    public JsonDriverCacheStore(
        ILogger<JsonDriverCacheStore> logger,
        string? overridePath = null,
        IOptionsMonitor<ScanCacheSettings>? scanCacheSettings = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _scanCacheSettings = scanCacheSettings;
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

    public event EventHandler? Cleared;

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

    internal static TimeSpan? ResolveRetentionWindow(ScanCacheSettings? settings)
    {
        if (settings is null || !settings.ExpirationEnabled)
        {
            return null;
        }

        var hours = Math.Clamp(
            settings.RetentionHours,
            ScanCacheSettings.MinimumRetentionHours,
            ScanCacheSettings.MaximumRetentionHours);
        return TimeSpan.FromHours(hours);
    }

    public Task<DriverCacheSnapshot?> LoadAsync(CancellationToken cancellationToken = default) =>
        LoadCoreAsync(applyRetention: true, cancellationToken);

    private async Task<DriverCacheSnapshot?> LoadCoreAsync(bool applyRetention, CancellationToken cancellationToken)
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
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var snapshot = await JsonSerializer.DeserializeAsync<DriverCacheSnapshot>(stream, _serializerOptions, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Loaded driver cache from {Path}: {Count} entries, captured at {CapturedAt}",
                path, snapshot?.Entries.Count ?? 0, snapshot?.CapturedAt);
            return !applyRetention || snapshot is null ? snapshot : DiscardIfExpired(snapshot);
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

    // A saved scan is only worth showing while it still describes the machine. Expiry is applied
    // on read and deletes the file there and then, so a stale snapshot cannot surface even after
    // the app was closed for weeks. Cleared is deliberately not raised: expiry is not a
    // user-requested clear, and a scan running at that moment must keep its own results.
    private DriverCacheSnapshot? DiscardIfExpired(DriverCacheSnapshot snapshot)
    {
        if (ResolveRetentionWindow(_scanCacheSettings?.CurrentValue) is not { } window)
        {
            return snapshot;
        }

        var age = DateTimeOffset.UtcNow - snapshot.CapturedAt;
        if (age <= window)
        {
            return snapshot;
        }

        _logger.LogInformation(
            "Saved scan captured at {CapturedAt} is {AgeHours:F1}h old, past the {RetentionHours:F0}h retention window; deleting it",
            snapshot.CapturedAt,
            age.TotalHours,
            window.TotalHours);
        try
        {
            DeleteCacheFiles();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not delete the expired driver cache; ignoring it for this session");
        }

        return null;
    }

    public async Task SaveAsync(DriverCacheSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var directory = Path.GetDirectoryName(CachePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var writeLock = await AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        var tempPath = CachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, snapshot, _serializerOptions, cancellationToken).ConfigureAwait(false);
            }
            File.Move(tempPath, CachePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }

        _logger.LogInformation(
            "Saved driver cache at {Path}: {DriverCount} drivers, {UpdateCount} cached update result(s)",
            CachePath,
            snapshot.Entries.Count,
            snapshot.Entries.Count(entry => entry.AvailableUpdate is not null));
    }

    public async Task<int> ClearAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Driver cache clear requested for {Path}", CachePath);
        await using var writeLock = await AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        var snapshot = await LoadCoreAsync(applyRetention: false, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var cachedUpdateCount = snapshot?.Entries.Count(entry => entry.AvailableUpdate is not null) ?? 0;
        var deletedFileCount = DeleteCacheFiles();

        _logger.LogInformation(
            "Driver cache clear completed: {UpdateCount} cached update result(s), {FileCount} file(s) deleted",
            cachedUpdateCount,
            deletedFileCount);
        Cleared?.Invoke(this, EventArgs.Empty);
        return cachedUpdateCount;
    }

    private int DeleteCacheFiles()
    {
        var deletedFileCount = 0;
        var paths = new[]
        {
            CachePath,
            CachePath + ".tmp",
            _legacyCachePath,
            _legacyCachePath is null ? null : _legacyCachePath + ".tmp"
        };

        foreach (var path in paths
                     .Where(path => !string.IsNullOrWhiteSpace(path))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(path))
            {
                continue;
            }

            File.Delete(path);
            deletedFileCount++;
            _logger.LogInformation("Deleted driver cache file {Path}", path);
        }

        return deletedFileCount;
    }

    private async ValueTask<FileStream> AcquireWriteLockAsync(CancellationToken cancellationToken)
    {
        var lockPath = CachePath + ".lock";
        var directory = Path.GetDirectoryName(lockPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.Asynchronous);
            }
            catch (IOException)
            {
                await Task.Delay(WriteLockRetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
