using System.Text.Json;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using Microsoft.Extensions.Logging;

namespace DriverUpdater.Infrastructure.Cache;

public sealed class JsonPendingUpdateVerificationStore : IPendingUpdateVerificationStore
{
    public const string DefaultFolderName = "DriverUpdater";
    public const string FileName = "pending-update-verification.json";

    private readonly ILogger<JsonPendingUpdateVerificationStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _serializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = null
    };

    public JsonPendingUpdateVerificationStore(
        ILogger<JsonPendingUpdateVerificationStore> logger,
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

    public async Task SaveAsync(
        PendingUpdateVerificationBatch batch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(StorePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var tempPath = StorePath + ".tmp";
            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    batch,
                    _serializerOptions,
                    cancellationToken).ConfigureAwait(false);
            }
            File.Move(tempPath, StorePath, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PendingUpdateVerificationBatch?> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(StorePath))
            {
                return null;
            }

            try
            {
                await using var stream = File.OpenRead(StorePath);
                return await JsonSerializer.DeserializeAsync<PendingUpdateVerificationBatch>(
                    stream,
                    _serializerOptions,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not read pending update verification from {Path}", StorePath);
                return null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(StorePath))
            {
                File.Delete(StorePath);
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
