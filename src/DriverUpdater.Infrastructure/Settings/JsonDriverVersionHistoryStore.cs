using System.Text.Json;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using Microsoft.Extensions.Logging;

namespace DriverUpdater.Infrastructure.Settings;

/// <summary>
/// JSON-file implementation of <see cref="IDriverVersionHistoryStore"/>. Follows the same
/// atomic-write pattern as <see cref="JsonDriverUpdateExclusionStore"/>. The file stays small
/// because each (device, version) pair is one metadata record, never driver binaries.
/// </summary>
public sealed class JsonDriverVersionHistoryStore : IDriverVersionHistoryStore
{
    public const string DefaultFolderName = "DriverUpdater";
    public const string FileName = "driver-version-history.json";

    // A device rarely sees this many distinct versions; the cap only guards against a vendor
    // that re-versions weekly filling the file over years. Oldest LastSeenAt records go first.
    internal const int MaxVersionsPerDevice = 20;

    private readonly ILogger<JsonDriverVersionHistoryStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeProvider _clock;
    private readonly JsonSerializerOptions _serializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = null
    };

    public JsonDriverVersionHistoryStore(
        ILogger<JsonDriverVersionHistoryStore> logger,
        string? overridePath = null,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        StorePath = overridePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            DefaultFolderName,
            FileName);
    }

    public string StorePath { get; }

    public async Task RecordScanAsync(
        IReadOnlyList<DriverInfo> drivers,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(drivers);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var records = await LoadUnlockedAsync(cancellationToken).ConfigureAwait(false);
            var byKey = records.ToDictionary(
                r => Key(r.DeviceId, r.Version),
                r => r,
                StringComparer.OrdinalIgnoreCase);

            var now = _clock.GetUtcNow();
            var changed = false;
            foreach (var driver in drivers)
            {
                var version = driver.CurrentVersion?.ToString();
                if (string.IsNullOrWhiteSpace(driver.DeviceId) || string.IsNullOrWhiteSpace(version))
                {
                    continue;
                }

                var key = Key(driver.DeviceId, version);
                if (byKey.TryGetValue(key, out var existing))
                {
                    byKey[key] = existing with
                    {
                        DeviceName = string.IsNullOrWhiteSpace(driver.DeviceName) ? existing.DeviceName : driver.DeviceName,
                        InfName = string.IsNullOrWhiteSpace(driver.InfName) ? existing.InfName : driver.InfName,
                        LastSeenAt = now
                    };
                }
                else
                {
                    byKey[key] = new DriverVersionRecord(
                        DeviceId: driver.DeviceId,
                        DeviceName: driver.DeviceName,
                        Version: version,
                        DriverDate: driver.CurrentDate,
                        InfName: driver.InfName,
                        Provider: driver.Provider,
                        FirstSeenAt: now,
                        LastSeenAt: now);
                }
                changed = true;
            }

            if (!changed)
            {
                return;
            }

            var trimmed = byKey.Values
                .GroupBy(r => r.DeviceId, StringComparer.OrdinalIgnoreCase)
                .SelectMany(group => group
                    .OrderByDescending(r => r.LastSeenAt)
                    .Take(MaxVersionsPerDevice))
                .ToArray();

            await SaveUnlockedAsync(trimmed, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<DriverVersionRecord>> GetHistoryAsync(
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var records = await LoadUnlockedAsync(cancellationToken).ConfigureAwait(false);
            return records
                .Where(r => string.Equals(r.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(r => r.LastSeenAt)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<DriverVersionRecord>> LoadUnlockedAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(StorePath))
        {
            return Array.Empty<DriverVersionRecord>();
        }

        try
        {
            await using var stream = File.OpenRead(StorePath);
            var records = await JsonSerializer
                .DeserializeAsync<List<DriverVersionRecord>>(stream, _serializerOptions, cancellationToken)
                .ConfigureAwait(false);
            return records?.Where(r => r is { DeviceId.Length: > 0, Version.Length: > 0 }).ToArray()
                ?? Array.Empty<DriverVersionRecord>();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A corrupt history file must never block scans; versions re-accumulate naturally.
            _logger.LogWarning(ex, "Could not read driver version history from {Path}; starting fresh", StorePath);
            return Array.Empty<DriverVersionRecord>();
        }
    }

    private async Task SaveUnlockedAsync(
        IReadOnlyList<DriverVersionRecord> records,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(StorePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = StorePath + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer
                .SerializeAsync(stream, records, _serializerOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        File.Move(tempPath, StorePath, overwrite: true);

        _logger.LogDebug(
            "Saved driver version history with {Count} record(s) to {Path}",
            records.Count, StorePath);
    }

    private static string Key(string deviceId, string version) => deviceId + "|" + version;
}
