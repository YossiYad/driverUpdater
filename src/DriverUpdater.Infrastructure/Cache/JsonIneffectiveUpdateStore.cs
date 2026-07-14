using System.Text.Json;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using Microsoft.Extensions.Logging;

namespace DriverUpdater.Infrastructure.Cache;

/// <summary>
/// JSON-backed store of proven-ineffective updates (see <see cref="IIneffectiveUpdateStore"/>).
/// Mirrors <see cref="JsonDriverCacheStore"/>: a single indented JSON file under CommonAppData.
/// </summary>
public sealed class JsonIneffectiveUpdateStore : IIneffectiveUpdateStore
{
    public const string DefaultFolderName = "DriverUpdater";
    public const string FileName = "ineffective-updates.json";

    private readonly ILogger<JsonIneffectiveUpdateStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _serializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = null
    };

    public JsonIneffectiveUpdateStore(ILogger<JsonIneffectiveUpdateStore> logger, string? overridePath = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        StorePath = overridePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            DefaultFolderName,
            FileName);
    }

    public string StorePath { get; }

    public async Task<IReadOnlyList<IneffectiveUpdateRecord>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadUnlockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RecordAsync(string deviceId, string targetVersion, string? installedVersion, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(targetVersion))
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var records = (await LoadUnlockedAsync(cancellationToken).ConfigureAwait(false)).ToList();
            var index = records.FindIndex(r =>
                string.Equals(r.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(r.TargetVersion, targetVersion, StringComparison.OrdinalIgnoreCase));

            if (index >= 0)
            {
                var existing = records[index];
                records[index] = existing with
                {
                    InstalledVersionAtAttempt = installedVersion,
                    AttemptCount = existing.AttemptCount + 1,
                    LastAttemptUtc = DateTimeOffset.UtcNow
                };
            }
            else
            {
                records.Add(new IneffectiveUpdateRecord(deviceId, targetVersion, installedVersion, 1, DateTimeOffset.UtcNow));
            }

            await SaveUnlockedAsync(records, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Recorded proven-ineffective update for {DeviceId}: target {Target} left installed version at {Installed}",
                deviceId, targetVersion, installedVersion ?? "unknown");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<IneffectiveUpdateRecord>> LoadUnlockedAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(StorePath))
        {
            return Array.Empty<IneffectiveUpdateRecord>();
        }

        try
        {
            await using var stream = File.OpenRead(StorePath);
            var records = await JsonSerializer.DeserializeAsync<List<IneffectiveUpdateRecord>>(stream, _serializerOptions, cancellationToken).ConfigureAwait(false);
            return records ?? (IReadOnlyList<IneffectiveUpdateRecord>)Array.Empty<IneffectiveUpdateRecord>();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not parse {Path}; ignoring the ineffective-update ledger", StorePath);
            return Array.Empty<IneffectiveUpdateRecord>();
        }
    }

    private async Task SaveUnlockedAsync(IReadOnlyList<IneffectiveUpdateRecord> records, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(StorePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = StorePath + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, records, _serializerOptions, cancellationToken).ConfigureAwait(false);
        }
        File.Move(tempPath, StorePath, overwrite: true);
    }
}
