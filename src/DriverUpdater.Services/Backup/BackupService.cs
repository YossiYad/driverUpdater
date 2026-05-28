using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using DriverUpdater.Core.Options;
using DriverUpdater.Core.Results;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DriverUpdater.Services.Backup;

public sealed class BackupService : IBackupService
{
    public const string DefaultRootFolderName = "DriverUpdater";

    private readonly IPnPUtilRunner _pnputil;
    private readonly IOptionsMonitor<BackupSettings> _settings;
    private readonly ILogger<BackupService> _logger;
    private readonly TimeProvider _clock;

    public BackupService(
        IPnPUtilRunner pnputil,
        IOptionsMonitor<BackupSettings> settings,
        ILogger<BackupService> logger,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(pnputil);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);
        _pnputil = pnputil;
        _settings = settings;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<Result<BackupArtifact>> BackupDriverAsync(DriverInfo driver, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(driver);

        if (string.IsNullOrWhiteSpace(driver.InfName))
        {
            return ResultError.From("BACKUP_NO_INF", $"Driver '{driver.DeviceName}' has no INF name, cannot back up.");
        }

        var root = ResolveRoot();
        var timestamp = _clock.GetUtcNow().ToString("yyyyMMddTHHmmssZ");
        var safeDevice = SanitizeFolderName(driver.DeviceName, driver.DeviceId);
        var destination = Path.Combine(root, timestamp, safeDevice);

        try
        {
            Directory.CreateDirectory(destination);
        }
        catch (Exception ex)
        {
            return ResultError.From("BACKUP_CREATE_DIR", ex);
        }

        var arguments = $"/export-driver \"{driver.InfName}\" \"{destination}\"";
        var result = await _pnputil.RunAsync(arguments, cancellationToken).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            _logger.LogWarning("pnputil export-driver exit {Code}: {Err}", result.ExitCode, result.StandardError);
            return ResultError.From(
                "BACKUP_PNPUTIL_FAILED",
                $"pnputil exit {result.ExitCode}: {result.StandardError.Trim()}");
        }

        var size = CalculateFolderSize(destination);
        var artifact = new BackupArtifact(
            DriverInfName: driver.InfName,
            DriverDeviceName: driver.DeviceName,
            BackupFolderPath: destination,
            CreatedAt: _clock.GetUtcNow(),
            SizeBytes: size);

        _logger.LogInformation("Backed up {Device} ({Inf}) to {Path} ({Size} bytes)",
            driver.DeviceName, driver.InfName, destination, size);
        return artifact;
    }

    public async Task<Result<bool>> RestoreFromBackupAsync(BackupArtifact artifact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);

        if (!Directory.Exists(artifact.BackupFolderPath))
        {
            return ResultError.From("BACKUP_NOT_FOUND", $"Backup folder '{artifact.BackupFolderPath}' is gone.");
        }

        var infFiles = Directory.GetFiles(artifact.BackupFolderPath, "*.inf", SearchOption.AllDirectories);
        if (infFiles.Length == 0)
        {
            return ResultError.From("BACKUP_NO_INF_FILES", "Backup folder contains no INF files.");
        }

        foreach (var inf in infFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var arguments = $"/add-driver \"{inf}\" /install";
            var result = await _pnputil.RunAsync(arguments, cancellationToken).ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                _logger.LogError("pnputil add-driver failed for {Inf}: exit {Code}, {Err}",
                    inf, result.ExitCode, result.StandardError);
                return ResultError.From(
                    "RESTORE_PNPUTIL_FAILED",
                    $"pnputil add-driver exit {result.ExitCode}: {result.StandardError.Trim()}");
            }
        }

        _logger.LogInformation("Restored {Device} from {Path}", artifact.DriverDeviceName, artifact.BackupFolderPath);
        return true;
    }

    public IReadOnlyList<BackupArtifact> ListBackups()
    {
        var root = ResolveRoot();
        if (!Directory.Exists(root))
        {
            return Array.Empty<BackupArtifact>();
        }

        var artifacts = new List<BackupArtifact>();
        foreach (var timestampFolder in Directory.GetDirectories(root))
        {
            foreach (var deviceFolder in Directory.GetDirectories(timestampFolder))
            {
                var info = new DirectoryInfo(deviceFolder);
                var firstInf = Directory.GetFiles(deviceFolder, "*.inf", SearchOption.AllDirectories).FirstOrDefault();
                var size = CalculateFolderSize(deviceFolder);
                artifacts.Add(new BackupArtifact(
                    DriverInfName: firstInf is null ? string.Empty : Path.GetFileName(firstInf),
                    DriverDeviceName: info.Name,
                    BackupFolderPath: deviceFolder,
                    CreatedAt: info.CreationTimeUtc,
                    SizeBytes: size));
            }
        }
        return artifacts;
    }

    public int PurgeBackupsOlderThan(TimeSpan age)
    {
        var root = ResolveRoot();
        if (!Directory.Exists(root))
        {
            return 0;
        }

        var cutoff = _clock.GetUtcNow() - age;
        var removed = 0;

        foreach (var timestampFolder in Directory.GetDirectories(root))
        {
            var info = new DirectoryInfo(timestampFolder);
            if (info.CreationTimeUtc <= cutoff.UtcDateTime)
            {
                try
                {
                    info.Delete(recursive: true);
                    removed++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to remove backup {Path}", info.FullName);
                }
            }
        }
        return removed;
    }

    internal string ResolveRoot()
    {
        var configured = _settings.CurrentValue.RootPath;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            DefaultRootFolderName,
            "Backups");
    }

    internal static string SanitizeFolderName(string deviceName, string fallback)
    {
        var raw = string.IsNullOrWhiteSpace(deviceName) ? fallback : deviceName;
        var invalid = Path.GetInvalidFileNameChars();
        var chars = raw.Select(c => invalid.Contains(c) || c == ' ' ? '_' : c).ToArray();
        var sanitized = new string(chars).Trim('_');
        return sanitized.Length == 0 ? "device" : sanitized;
    }

    private static long CalculateFolderSize(string path)
    {
        if (!Directory.Exists(path))
        {
            return 0;
        }
        long total = 0;
        foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
        {
            try
            {
                total += new FileInfo(file).Length;
            }
            catch
            {
            }
        }
        return total;
    }
}
