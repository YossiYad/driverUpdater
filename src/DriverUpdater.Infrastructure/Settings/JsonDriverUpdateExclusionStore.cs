using System.Text.Json;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using Microsoft.Extensions.Logging;

namespace DriverUpdater.Infrastructure.Settings;

/// <summary>
/// JSON-backed store of the devices excluded from updating (see <see cref="IDriverUpdateExclusionStore"/>).
/// Written under CommonAppData so the scheduled task, which runs as SYSTEM, reads the same file
/// the interactive app wrote.
/// </summary>
public sealed class JsonDriverUpdateExclusionStore : IDriverUpdateExclusionStore
{
    public const string DefaultFolderName = "DriverUpdater";
    public const string FileName = "excluded-drivers.json";

    private readonly ILogger<JsonDriverUpdateExclusionStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _serializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = null
    };

    public JsonDriverUpdateExclusionStore(
        ILogger<JsonDriverUpdateExclusionStore> logger,
        string? overridePath = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        StorePath = overridePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            DefaultFolderName,
            FileName);
    }

    public string StorePath { get; }

    public async Task<DriverUpdateExclusions> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(StorePath))
            {
                return DriverUpdateExclusions.Empty;
            }

            await using var stream = File.OpenRead(StorePath);
            var exclusions = await JsonSerializer
                .DeserializeAsync<DriverUpdateExclusions>(stream, _serializerOptions, cancellationToken)
                .ConfigureAwait(false);
            return Normalize(exclusions);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not read driver update exclusions from {Path}", StorePath);
            throw new InvalidDataException(
                $"Could not read driver update exclusions from '{StorePath}'.",
                ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(DriverUpdateExclusions exclusions, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exclusions);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(StorePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var normalized = Normalize(exclusions);
            var tempPath = StorePath + ".tmp";
            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer
                    .SerializeAsync(stream, normalized, _serializerOptions, cancellationToken)
                    .ConfigureAwait(false);
            }
            File.Move(tempPath, StorePath, overwrite: true);

            _logger.LogInformation(
                "Saved driver update exclusions with {Count} device(s) to {Path}",
                normalized.DeviceIds.Count, StorePath);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static DriverUpdateExclusions Normalize(DriverUpdateExclusions? exclusions)
    {
        if (exclusions?.DeviceIds is not { Count: > 0 } ids)
        {
            return DriverUpdateExclusions.Empty;
        }

        return new DriverUpdateExclusions(ids
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray());
    }
}
