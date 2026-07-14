using System.Text.Json;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Options;
using Microsoft.Extensions.Logging;

namespace DriverUpdater.Infrastructure.Settings;

public sealed class JsonSettingsStore : ISettingsStore
{
    public const string DefaultFolderName = "DriverUpdater";
    public const string SettingsFileName = "settings.json";

    private readonly ILogger<JsonSettingsStore> _logger;
    private readonly JsonSerializerOptions _serializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = null
    };

    public JsonSettingsStore(ILogger<JsonSettingsStore> logger, string? overridePath = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        SettingsPath = overridePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            DefaultFolderName,
            SettingsFileName);
    }

    public string SettingsPath { get; }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(SettingsPath))
        {
            _logger.LogInformation("No settings file at {Path}; using defaults", SettingsPath);
            return new AppSettings();
        }

        try
        {
            await using var stream = File.OpenRead(SettingsPath);
            var settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, _serializerOptions, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Loaded settings from {Path}", SettingsPath);
            return settings ?? new AppSettings();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not parse settings.json, using defaults");
            return new AppSettings();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var directory = Path.GetDirectoryName(SettingsPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = SettingsPath + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, settings, _serializerOptions, cancellationToken).ConfigureAwait(false);
        }
        File.Move(tempPath, SettingsPath, overwrite: true);

        _logger.LogInformation("Saved settings to {Path}", SettingsPath);
    }
}
