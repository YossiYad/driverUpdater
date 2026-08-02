using System.Text.Json;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using Microsoft.Extensions.Logging;

namespace DriverUpdater.Infrastructure.Settings;

/// <summary>
/// JSON-backed store of the per-driver auto-update opt-in (see <see cref="IAutoUpdateSelectionStore"/>).
/// Written under CommonAppData so the scheduled task, which runs as SYSTEM, reads the same file
/// the interactive app wrote.
/// </summary>
public sealed class JsonAutoUpdateSelectionStore : IAutoUpdateSelectionStore
{
    public const string DefaultFolderName = "DriverUpdater";
    public const string FileName = "auto-update-selection.json";

    private readonly ILogger<JsonAutoUpdateSelectionStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _serializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = null
    };

    public JsonAutoUpdateSelectionStore(ILogger<JsonAutoUpdateSelectionStore> logger, string? overridePath = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        StorePath = overridePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            DefaultFolderName,
            FileName);
    }

    public string StorePath { get; }

    public async Task<AutoUpdateSelection> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(StorePath))
            {
                return AutoUpdateSelection.Empty;
            }

            await using var stream = File.OpenRead(StorePath);
            var selection = await JsonSerializer
                .DeserializeAsync<AutoUpdateSelection>(stream, _serializerOptions, cancellationToken)
                .ConfigureAwait(false);
            return Normalize(selection);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not parse {Path}; no driver is treated as opted in", StorePath);
            return AutoUpdateSelection.Empty;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(AutoUpdateSelection selection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(StorePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var normalized = Normalize(selection);
            var tempPath = StorePath + ".tmp";
            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer
                    .SerializeAsync(stream, normalized, _serializerOptions, cancellationToken)
                    .ConfigureAwait(false);
            }
            File.Move(tempPath, StorePath, overwrite: true);

            _logger.LogInformation(
                "Saved auto-update selection with {Count} device(s) to {Path}",
                normalized.DeviceIds.Count, StorePath);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static AutoUpdateSelection Normalize(AutoUpdateSelection? selection)
    {
        if (selection?.DeviceIds is not { Count: > 0 } ids)
        {
            return AutoUpdateSelection.Empty;
        }

        return new AutoUpdateSelection(ids
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray());
    }
}
