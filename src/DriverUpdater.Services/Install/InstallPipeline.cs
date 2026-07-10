using System.IO.Compression;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using Microsoft.Extensions.Logging;

namespace DriverUpdater.Services.Install;

public sealed class InstallPipeline : IInstallPipeline
{
    public const string DownloadsHttpClientName = "VendorInstallerDownloads";

    // Circuit breaker: after the first restore-point failure (e.g. srservice disabled)
    // subsequent drivers skip the attempt instead of flooding the log with the same error.
    private int _restorePointSuppressed;

    private readonly IRestorePointService _restorePointService;
    private readonly IBackupService _backupService;
    private readonly IWuApiClient _wuApiClient;
    private readonly IPnPUtilRunner? _pnputil;
    private readonly IPowerShellInvoker? _powerShell;
    private readonly IVendorInstallerRunner? _vendorInstallerRunner;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly IHistoryRepository? _historyRepository;
    private readonly IVendorPageInstallerResolver? _vendorPageResolver;
    private readonly IInstalledDriverProbe? _installedDriverProbe;
    private readonly ILogger<InstallPipeline> _logger;
    private readonly TimeProvider _clock;

    public InstallPipeline(
        IRestorePointService restorePointService,
        IBackupService backupService,
        IWuApiClient wuApiClient,
        ILogger<InstallPipeline> logger,
        IPnPUtilRunner? pnputil = null,
        IPowerShellInvoker? powerShell = null,
        IVendorInstallerRunner? vendorInstallerRunner = null,
        IHttpClientFactory? httpClientFactory = null,
        IHistoryRepository? historyRepository = null,
        TimeProvider? clock = null,
        IVendorPageInstallerResolver? vendorPageResolver = null,
        IInstalledDriverProbe? installedDriverProbe = null)
    {
        ArgumentNullException.ThrowIfNull(restorePointService);
        ArgumentNullException.ThrowIfNull(backupService);
        ArgumentNullException.ThrowIfNull(wuApiClient);
        ArgumentNullException.ThrowIfNull(logger);
        _restorePointService = restorePointService;
        _backupService = backupService;
        _wuApiClient = wuApiClient;
        _pnputil = pnputil;
        _powerShell = powerShell;
        _vendorInstallerRunner = vendorInstallerRunner;
        _httpClientFactory = httpClientFactory;
        _historyRepository = historyRepository;
        _vendorPageResolver = vendorPageResolver;
        _installedDriverProbe = installedDriverProbe;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<UpdateOperation> ExecuteAsync(
        UpdateOperation operation,
        InstallOptions options,
        IProgress<UpdateOperation>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(options);

        var recordingProgress = WrapWithRecorder(progress, cancellationToken);

        try
        {
            if (options.DryRun)
            {
                _logger.LogInformation("Dry run for {Device}", operation.TargetSnapshot.DeviceName);
                operation = operation with
                {
                    Status = UpdateStatus.Skipped,
                    ErrorMessage = BuildDryRunSummary(operation, options),
                    CompletedAt = _clock.GetUtcNow()
                };
                recordingProgress.Report(operation);
                return operation;
            }

            if (options.CreateRestorePoint)
            {
                operation = await StepCreateRestorePointAsync(operation, recordingProgress, cancellationToken).ConfigureAwait(false);
                if (operation.Status == UpdateStatus.Failed)
                {
                    return operation;
                }
            }

            if (options.BackupCurrentDriver)
            {
                operation = await StepBackupAsync(operation, recordingProgress, cancellationToken).ConfigureAwait(false);
                if (operation.Status == UpdateStatus.Failed)
                {
                    return operation;
                }
            }

            operation = await StepDownloadAndInstallAsync(operation, recordingProgress, cancellationToken).ConfigureAwait(false);

            if (operation.Status == UpdateStatus.Succeeded)
            {
                operation = await StepVerifyInstallAsync(operation, recordingProgress, cancellationToken).ConfigureAwait(false);
            }

            return operation;
        }
        catch (OperationCanceledException)
        {
            operation = operation with
            {
                Status = UpdateStatus.Cancelled,
                ErrorMessage = "Operation cancelled by user.",
                CompletedAt = _clock.GetUtcNow()
            };
            recordingProgress.Report(operation);
            return operation;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected install pipeline failure");
            operation = operation with
            {
                Status = UpdateStatus.Failed,
                ErrorMessage = $"Unexpected error: {ex.Message}",
                CompletedAt = _clock.GetUtcNow()
            };
            recordingProgress.Report(operation);
            return operation;
        }
    }

    private IProgress<UpdateOperation> WrapWithRecorder(IProgress<UpdateOperation>? outer, CancellationToken cancellationToken) =>
        new RecordingProgress(outer, _historyRepository, _logger, cancellationToken);

    private sealed class RecordingProgress : IProgress<UpdateOperation>
    {
        private readonly IProgress<UpdateOperation>? _outer;
        private readonly IHistoryRepository? _repository;
        private readonly ILogger _logger;
        private readonly CancellationToken _cancellationToken;
        private UpdateStatus? _lastRecordedStatus;

        public RecordingProgress(IProgress<UpdateOperation>? outer, IHistoryRepository? repository, ILogger logger, CancellationToken cancellationToken)
        {
            _outer = outer;
            _repository = repository;
            _logger = logger;
            _cancellationToken = cancellationToken;
        }

        public void Report(UpdateOperation value)
        {
            _outer?.Report(value);
            if (_repository is null)
            {
                return;
            }
            // Skip per-byte download progress reports - the row's status hasn't moved, only
            // DownloadedBytes is ticking. Persisting every 256KB chunk to SQLite would be
            // ~300 writes for a 76MB download, all of which the UI does not care about.
            if (_lastRecordedStatus == value.Status && !value.IsTerminal)
            {
                return;
            }
            _lastRecordedStatus = value.Status;
            _ = Task.Run(async () =>
            {
                try
                {
                    await _repository.UpsertOperationAsync(value, _cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to record operation {Id}", value.OperationId);
                }
            }, CancellationToken.None);
        }
    }

    private async Task<UpdateOperation> StepCreateRestorePointAsync(
        UpdateOperation operation,
        IProgress<UpdateOperation>? progress,
        CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _restorePointSuppressed, 0, 0) == 1)
        {
            _logger.LogInformation("Restore point skipped for {Device} (suppressed: earlier failure in this session)", DeviceLabel(operation.TargetSnapshot));
            return operation;
        }

        operation = operation with { Status = UpdateStatus.CreatingRestorePoint };
        progress?.Report(operation);

        var description = $"DriverUpdater - before {DeviceLabel(operation.TargetSnapshot)}";
        var rp = await _restorePointService.CreateRestorePointAsync(description, cancellationToken).ConfigureAwait(false);
        if (rp.IsFailure)
        {
            Interlocked.Exchange(ref _restorePointSuppressed, 1);
            _logger.LogWarning(
                "Restore point creation failed ({Error}). System Protection is most likely turned off for the " +
                "system drive - enable it via System Properties > System Protection (or " +
                "'Enable-ComputerRestore -Drive \"C:\\\"' in an elevated PowerShell) to allow rollbacks. " +
                "Restore points will be skipped for all remaining drivers this session. " +
                "Driver file backups (pnputil export-driver) are unaffected and will still run.",
                rp.Error.Message);
            return operation;
        }

        operation = operation with { RestorePointSequenceNumber = rp.Value.SequenceNumber };
        progress?.Report(operation);
        return operation;
    }

    private async Task<UpdateOperation> StepBackupAsync(
        UpdateOperation operation,
        IProgress<UpdateOperation>? progress,
        CancellationToken cancellationToken)
    {
        operation = operation with { Status = UpdateStatus.BackingUp };
        progress?.Report(operation);

        var backup = await _backupService.BackupDriverAsync(operation.TargetSnapshot, cancellationToken).ConfigureAwait(false);
        if (backup.IsFailure)
        {
            // A driver with no INF name is a virtual/inbox device (e.g. "Microsoft Print to PDF")
            // that ships with Windows and has nothing to export - that is expected, not a problem
            // worth a warning. Genuine backup failures still surface as warnings.
            if (string.Equals(backup.Error.Code, "BACKUP_NO_INF", StringComparison.Ordinal))
            {
                _logger.LogInformation(
                    "Backup skipped for {Device}: virtual/inbox device has no INF to export (continuing with installation).",
                    operation.TargetSnapshot.DeviceName);
            }
            else
            {
                _logger.LogWarning("Backup step failed (continuing with installation): {Error}", backup.Error.Message);
            }
            return operation;
        }

        operation = operation with { BackupPath = backup.Value.BackupFolderPath };
        progress?.Report(operation);
        return operation;
    }

    // Confirms the active driver actually changed after a "successful" install, rather than
    // trusting the installer's exit code. pnputil can report success after merely staging a
    // package in the driver store while Windows keeps a higher-ranked (e.g. inbox) driver
    // bound - which is exactly why such "updates" reappeared on every scan.
    private async Task<UpdateOperation> StepVerifyInstallAsync(
        UpdateOperation operation,
        IProgress<UpdateOperation>? progress,
        CancellationToken cancellationToken)
    {
        if (_installedDriverProbe is null)
        {
            return operation;
        }

        var deviceName = DeviceLabel(operation.TargetSnapshot);

        // When a reboot is required the new driver only binds after restart, so an in-session
        // read-back would falsely report "unchanged". Defer verification to the next scan.
        if (operation.ErrorMessage?.Contains("reboot", StringComparison.OrdinalIgnoreCase) == true)
        {
            _logger.LogInformation(
                "Install verification deferred for {Device}: reboot required before the new driver binds.", deviceName);
            return operation;
        }

        var before = operation.TargetSnapshot;
        var current = await _installedDriverProbe.GetCurrentAsync(before.DeviceId, cancellationToken).ConfigureAwait(false);
        if (current is null || (current.Version is null && current.Date is null))
        {
            _logger.LogInformation(
                "Install verification inconclusive for {Device}: the current driver could not be read back.", deviceName);
            return operation;
        }

        var versionChanged = !Equals(current.Version, before.CurrentVersion);
        var dateChanged = current.Date != before.CurrentDate;
        if (versionChanged || dateChanged)
        {
            _logger.LogInformation(
                "Install verified for {Device}: active driver changed from {OldVersion} ({OldDate}) to {NewVersion} ({NewDate}).",
                deviceName,
                before.CurrentVersion?.ToString() ?? "?", before.CurrentDate?.ToString() ?? "?",
                current.Version?.ToString() ?? "?", current.Date?.ToString() ?? "?");
            return operation;
        }

        // Nothing changed: the package was staged but Windows kept the existing driver.
        var installedText = before.CurrentVersion?.ToString() ?? before.CurrentDate?.ToString() ?? "unknown";
        _logger.LogWarning(
            "Install did not take effect for {Device}: the active driver is still {Installed} after installing " +
            "{Source} {Candidate}. Windows kept the existing driver - usually because it is ranked higher (e.g. a " +
            "protected Windows inbox driver) or a reboot is pending. Reporting this as not applied rather than success.",
            deviceName, installedText, operation.Candidate.Source,
            operation.Candidate.NewVersion?.ToString() ?? "?");

        operation = operation with
        {
            Status = UpdateStatus.Skipped,
            ErrorMessage =
                $"Installed to the driver store, but Windows kept the existing driver (version unchanged: {installedText}). " +
                "This usually means the current driver is ranked higher (e.g. a protected Windows inbox driver) or a reboot is pending.",
            CompletedAt = _clock.GetUtcNow()
        };
        progress?.Report(operation);
        return operation;
    }

    private async Task<UpdateOperation> StepDownloadAndInstallAsync(
        UpdateOperation operation,
        IProgress<UpdateOperation>? progress,
        CancellationToken cancellationToken)
    {
        if (operation.Candidate.InstallKind == UpdateInstallKind.VendorPage)
        {
            var resolved = _vendorPageResolver is null
                ? null
                : await _vendorPageResolver.TryResolveAsync(operation.Candidate, cancellationToken).ConfigureAwait(false);
            if (resolved is not null)
            {
                _logger.LogInformation(
                    "Vendor page update for {Device} resolved to in-app installer {Url} ({SourceUpdateId})",
                    operation.TargetSnapshot.DeviceName, resolved.DownloadUrl, resolved.SourceUpdateId);
                operation = operation with { Candidate = resolved };
                return await StepInstallVendorInstallerAsync(operation, progress, cancellationToken).ConfigureAwait(false);
            }

            _logger.LogInformation(
                "Vendor page update for {Device} cannot be installed in-app ({Reason}); deferring to vendor page {Url}",
                operation.TargetSnapshot.DeviceName,
                _vendorPageResolver is null ? "no vendor page resolver configured" : "no direct installer found on the page",
                operation.Candidate.DownloadUrl);
            operation = operation with
            {
                Status = UpdateStatus.Skipped,
                ErrorMessage = $"Open the official vendor page to install this update: {operation.Candidate.DownloadUrl}",
                CompletedAt = _clock.GetUtcNow()
            };
            progress?.Report(operation);
            return operation;
        }

        if (operation.Candidate.InstallKind == UpdateInstallKind.PnPUtilPackage)
        {
            return await StepInstallPnPUtilPackageAsync(operation, progress, cancellationToken).ConfigureAwait(false);
        }

        if (operation.Candidate.InstallKind == UpdateInstallKind.VendorInstaller)
        {
            return await StepInstallVendorInstallerAsync(operation, progress, cancellationToken).ConfigureAwait(false);
        }

        if (operation.Candidate.Source != UpdateSource.WindowsUpdate)
        {
            operation = operation with
            {
                Status = UpdateStatus.Skipped,
                ErrorMessage = $"{operation.Candidate.Source} installs are not yet supported by the pipeline.",
                CompletedAt = _clock.GetUtcNow()
            };
            progress?.Report(operation);
            return operation;
        }

        operation = operation with { Status = UpdateStatus.Downloading };
        progress?.Report(operation);

        operation = operation with { Status = UpdateStatus.Installing };
        progress?.Report(operation);

        var install = await _wuApiClient.DownloadAndInstallAsync(operation.Candidate.SourceUpdateId, cancellationToken).ConfigureAwait(false);
        if (install.IsFailure)
        {
            _logger.LogError("Install failed: {Error}", install.Error);
            operation = operation with
            {
                Status = UpdateStatus.Failed,
                ErrorMessage = install.Error.Message,
                CompletedAt = _clock.GetUtcNow()
            };
            progress?.Report(operation);
            return operation;
        }

        operation = operation with
        {
            Status = UpdateStatus.Succeeded,
            ErrorMessage = install.Value.RebootRequired ? "Reboot required to complete installation." : null,
            CompletedAt = _clock.GetUtcNow()
        };
        progress?.Report(operation);
        return operation;
    }

    private async Task<UpdateOperation> StepInstallVendorInstallerAsync(
        UpdateOperation operation,
        IProgress<UpdateOperation>? progress,
        CancellationToken cancellationToken)
    {
        if (_vendorInstallerRunner is null || _httpClientFactory is null)
        {
            operation = operation with
            {
                Status = UpdateStatus.Failed,
                ErrorMessage = "Vendor installer services are not configured.",
                CompletedAt = _clock.GetUtcNow()
            };
            progress?.Report(operation);
            return operation;
        }

        operation = operation with { Status = UpdateStatus.Downloading, DownloadedBytes = 0, TotalBytes = null };
        progress?.Report(operation);

        var workDir = Path.Combine(Path.GetTempPath(), "DriverUpdater", operation.OperationId.ToString("N"));
        try
        {
            var installerPath = await DownloadPackageAsync(
                operation.Candidate.DownloadUrl,
                workDir,
                (bytes, total) =>
                {
                    operation = operation with { DownloadedBytes = bytes, TotalBytes = total };
                    progress?.Report(operation);
                },
                cancellationToken).ConfigureAwait(false);
            if (Path.GetExtension(installerPath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                var locatedInstaller = ExtractZipAndLocateInstaller(installerPath, workDir, out var extractionError);
                if (locatedInstaller is null)
                {
                    operation = operation with
                    {
                        Status = UpdateStatus.Skipped,
                        ErrorMessage = extractionError,
                        CompletedAt = _clock.GetUtcNow()
                    };
                    progress?.Report(operation);
                    return operation;
                }
                installerPath = locatedInstaller;
            }

            if (!TryBuildVendorInstallerCommand(operation.Candidate, installerPath, out var fileName, out var arguments, out var skipReason))
            {
                operation = operation with
                {
                    Status = UpdateStatus.Skipped,
                    ErrorMessage = skipReason,
                    CompletedAt = _clock.GetUtcNow()
                };
                progress?.Report(operation);
                return operation;
            }

            if (Path.GetExtension(installerPath).Equals(".exe", StringComparison.OrdinalIgnoreCase)
                && !HasPortableExecutableMagic(installerPath))
            {
                _logger.LogError("Downloaded file {Path} is not a valid PE - vendor CDN likely served HTML instead of the installer", installerPath);
                operation = operation with
                {
                    Status = UpdateStatus.Failed,
                    ErrorMessage = $"Downloaded file is not a valid Windows executable. The vendor's CDN likely returned an HTML page instead of the installer at {operation.Candidate.DownloadUrl}.",
                    CompletedAt = _clock.GetUtcNow()
                };
                progress?.Report(operation);
                return operation;
            }

            operation = operation with { Status = UpdateStatus.Installing, InstallStartedAt = _clock.GetUtcNow() };
            progress?.Report(operation);

            _logger.LogInformation(
                "Vendor installer for {Device}: starting \"{FileName}\" {Arguments} (current driver: version={CurrentVersion}, date={CurrentDate}; target: version={TargetVersion}, date={TargetDate})",
                operation.TargetSnapshot.DeviceName, fileName, arguments,
                operation.TargetSnapshot.CurrentVersion, operation.TargetSnapshot.CurrentDate,
                operation.Candidate.NewVersion, operation.Candidate.NewDate);

            var installStart = _clock.GetUtcNow();
            var result = await _vendorInstallerRunner.RunAsync(fileName, arguments, cancellationToken).ConfigureAwait(false);
            var installElapsed = _clock.GetUtcNow() - installStart;
            if (!result.IsSuccess)
            {
                var harvestedLogs = TryHarvestInstallerLogTails(installStart, operation.Candidate.SourceUpdateId);
                _logger.LogError(
                    "Vendor installer for {Device} failed after {Elapsed}: exit {Code}\n  stdout: {Stdout}\n  stderr: {Stderr}{HarvestedLogs}",
                    operation.TargetSnapshot.DeviceName, installElapsed, result.ExitCode,
                    string.IsNullOrWhiteSpace(result.StandardOutput) ? "<empty>" : result.StandardOutput,
                    string.IsNullOrWhiteSpace(result.StandardError) ? "<empty>" : result.StandardError,
                    string.IsNullOrEmpty(harvestedLogs) ? string.Empty : "\n  vendor log tails:\n" + harvestedLogs);
                operation = operation with
                {
                    Status = UpdateStatus.Failed,
                    ErrorMessage = $"Vendor installer exit {result.ExitCode}: {FirstNonEmpty(result.StandardError, result.StandardOutput)}",
                    CompletedAt = _clock.GetUtcNow()
                };
                progress?.Report(operation);
                return operation;
            }

            _logger.LogInformation(
                "Vendor installer for {Device} succeeded after {Elapsed}",
                operation.TargetSnapshot.DeviceName, installElapsed);
            operation = operation with
            {
                Status = UpdateStatus.Succeeded,
                CompletedAt = _clock.GetUtcNow()
            };
            progress?.Report(operation);
            return operation;
        }
        finally
        {
            TryDeleteWorkDirectory(workDir);
        }
    }

    private async Task<UpdateOperation> StepInstallPnPUtilPackageAsync(
        UpdateOperation operation,
        IProgress<UpdateOperation>? progress,
        CancellationToken cancellationToken)
    {
        if (_pnputil is null || _powerShell is null || _httpClientFactory is null)
        {
            operation = operation with
            {
                Status = UpdateStatus.Failed,
                ErrorMessage = "Catalog install services are not configured.",
                CompletedAt = _clock.GetUtcNow()
            };
            progress?.Report(operation);
            return operation;
        }

        operation = operation with { Status = UpdateStatus.Downloading, DownloadedBytes = 0, TotalBytes = null };
        progress?.Report(operation);

        var workDir = Path.Combine(Path.GetTempPath(), "DriverUpdater", operation.OperationId.ToString("N"));
        try
        {
            var packagePath = await DownloadPackageAsync(
                operation.Candidate.DownloadUrl,
                workDir,
                (bytes, total) =>
                {
                    operation = operation with { DownloadedBytes = bytes, TotalBytes = total };
                    progress?.Report(operation);
                },
                cancellationToken).ConfigureAwait(false);

            var packageExt = Path.GetExtension(packagePath);
            if (!packageExt.Equals(".cab", StringComparison.OrdinalIgnoreCase)
                && !packageExt.Equals(".inf", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "pnputil package for {Device} downloaded as {Ext} - only .cab/.inf are supported for automatic install; skipping. URL: {Url}",
                    operation.TargetSnapshot.DeviceName, packageExt, operation.Candidate.DownloadUrl);
                operation = operation with
                {
                    Status = UpdateStatus.Skipped,
                    ErrorMessage = $"Package format '{packageExt}' is not supported by pnputil. Open the vendor page to install manually: {operation.Candidate.DownloadUrl}",
                    CompletedAt = _clock.GetUtcNow()
                };
                progress?.Report(operation);
                return operation;
            }

            var installRoot = await PreparePnPUtilInstallRootAsync(packagePath, workDir, cancellationToken).ConfigureAwait(false);

            operation = operation with { Status = UpdateStatus.Installing, InstallStartedAt = _clock.GetUtcNow() };
            progress?.Report(operation);

            var addDriverArgs = $"/add-driver \"{Path.Combine(installRoot, "*.inf")}\" /subdirs /install";
            var result = await _pnputil.RunAsync(addDriverArgs, cancellationToken).ConfigureAwait(false);

            // Exit 3010 = ERROR_SUCCESS_REBOOT_REQUIRED: driver staged, reboot needed.
            // Exit 259 = ERROR_NO_MORE_ITEMS, returned by pnputil on some Windows builds
            // (notably Intel SST/HDA components) when a prior pending reboot must be
            // completed before the INF is fully applied. The pnputil output explicitly
            // says "System reboot is needed to complete install operations!" in this case.
            if (result.ExitCode is 3010 or 259)
            {
                _logger.LogInformation("pnputil catalog install succeeded with reboot required (exit {Code})", result.ExitCode);
                operation = operation with
                {
                    Status = UpdateStatus.Succeeded,
                    ErrorMessage = "Reboot required to complete driver installation.",
                    CompletedAt = _clock.GetUtcNow()
                };
                progress?.Report(operation);
                return operation;
            }

            if (!result.IsSuccess)
            {
                _logger.LogError("pnputil catalog install failed: exit {Code}, {Err}", result.ExitCode, result.StandardError);
                operation = operation with
                {
                    Status = UpdateStatus.Failed,
                    ErrorMessage = $"pnputil install exit {result.ExitCode}: {FirstNonEmpty(result.StandardError, result.StandardOutput)}",
                    CompletedAt = _clock.GetUtcNow()
                };
                progress?.Report(operation);
                return operation;
            }

            operation = operation with
            {
                Status = UpdateStatus.Succeeded,
                CompletedAt = _clock.GetUtcNow()
            };
            progress?.Report(operation);
            return operation;
        }
        finally
        {
            TryDeleteWorkDirectory(workDir);
        }
    }

    private void TryDeleteWorkDirectory(string workDir)
    {
        try
        {
            if (Directory.Exists(workDir))
            {
                Directory.Delete(workDir, recursive: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to remove temporary install directory {Path}", workDir);
        }
    }

    private async Task<string> DownloadPackageAsync(
        Uri downloadUrl,
        string workDir,
        Action<long, long?>? onProgress,
        CancellationToken cancellationToken)
    {
        if (downloadUrl.IsFile)
        {
            return downloadUrl.LocalPath;
        }

        Directory.CreateDirectory(workDir);

        var fileName = Path.GetFileName(downloadUrl.LocalPath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = "driver-package.cab";
        }

        var packagePath = Path.Combine(workDir, fileName);
        var client = _httpClientFactory!.CreateClient(DownloadsHttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
        // AMD's CDN checks Referer for anti-hotlinking. Send the download URL's own
        // scheme+host as the Referer so AMD treats us as having clicked through from
        // their own page instead of redirecting to "Download-Incomplete.html".
        request.Headers.Referrer = new Uri($"{downloadUrl.Scheme}://{downloadUrl.Host}/");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength;
        onProgress?.Invoke(0, totalBytes);

        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var output = File.Create(packagePath))
        {
            var buffer = new byte[81920];
            long downloaded = 0;
            long lastReported = 0;
            var lastReportAt = _clock.GetUtcNow();
            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                downloaded += read;

                if (onProgress is not null)
                {
                    var now = _clock.GetUtcNow();
                    if (downloaded - lastReported >= 262_144 || (now - lastReportAt) >= TimeSpan.FromMilliseconds(150))
                    {
                        onProgress(downloaded, totalBytes);
                        lastReported = downloaded;
                        lastReportAt = now;
                    }
                }
            }

            onProgress?.Invoke(downloaded, totalBytes ?? downloaded);
        }

        var info = new FileInfo(packagePath);
        _logger.LogInformation("Downloaded {Bytes} bytes from {Url} to {Path} (content-type {ContentType})",
            info.Length, downloadUrl, packagePath, response.Content.Headers.ContentType?.MediaType ?? "<unknown>");

        return packagePath;
    }

    internal static bool HasPortableExecutableMagic(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var header = new byte[2];
            if (stream.Read(header, 0, 2) != 2)
            {
                return false;
            }
            return header[0] == 0x4D && header[1] == 0x5A;
        }
        catch
        {
            return false;
        }
    }

    private async Task<string> PreparePnPUtilInstallRootAsync(string packagePath, string workDir, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(packagePath);
        if (extension.Equals(".inf", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetDirectoryName(packagePath) ?? workDir;
        }

        if (!extension.Equals(".cab", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Catalog package type '{extension}' is not supported for automatic install.");
        }

        var extractDir = Path.Combine(workDir, "expanded");
        Directory.CreateDirectory(extractDir);

        var expandPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "expand.exe");
        var script = $"& {QuotePowerShellLiteral(expandPath)} -F:* {QuotePowerShellLiteral(packagePath)} {QuotePowerShellLiteral(extractDir)}; exit $LASTEXITCODE";
        var result = await _powerShell!.InvokeAsync(script, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException($"expand.exe exit {result.ExitCode}: {FirstNonEmpty(result.StandardError, result.StandardOutput)}");
        }

        if (!Directory.EnumerateFiles(extractDir, "*.inf", SearchOption.AllDirectories).Any())
        {
            throw new InvalidOperationException("Catalog package did not contain any INF files.");
        }

        return extractDir;
    }

    private static string FirstNonEmpty(string first, string second)
    {
        var value = string.IsNullOrWhiteSpace(first) ? second : first;
        return value.Trim();
    }

    // A human-readable label for a device that never yields an empty string. Virtual/inbox
    // devices (e.g. Microsoft Print to PDF) can have a blank DeviceName, which otherwise leaks
    // into the restore point description ("DriverUpdater - before ") and logs.
    private static string DeviceLabel(DriverInfo driver)
    {
        if (!string.IsNullOrWhiteSpace(driver.DeviceName))
        {
            return driver.DeviceName;
        }
        if (!string.IsNullOrWhiteSpace(driver.HardwareId))
        {
            return driver.HardwareId;
        }
        return string.IsNullOrWhiteSpace(driver.DeviceId) ? "Unknown device" : driver.DeviceId;
    }

    // When a silent installer exits non-zero with empty stdout/stderr (AMD and most
    // wrapper installers do this) the only real evidence is in log files the installer
    // leaves under %TEMP%, %LOCALAPPDATA%, or %WINDIR%\Logs. Scan those dirs for files
    // created since the install started, and pull the tail of each so the user sees
    // the actual cause in the app log instead of a bare "exit 2, <empty>".
    internal static string TryHarvestInstallerLogTails(DateTimeOffset installStart, string sourceUpdateId)
    {
        var search = new List<string>();
        TryAddIfExists(search, Path.GetTempPath());
        TryAddIfExists(search, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        TryAddIfExists(search, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Logs"));

        // Per-vendor hint folders.
        if (sourceUpdateId.Contains("amd", StringComparison.OrdinalIgnoreCase))
        {
            TryAddIfExists(search, @"C:\AMD");
            TryAddIfExists(search, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AMD"));
        }

        var sb = new System.Text.StringBuilder();
        var cutoff = installStart.UtcDateTime.AddSeconds(-2);
        foreach (var dir in search)
        {
            try
            {
                var hits = Directory.EnumerateFiles(dir, "*.log", SearchOption.TopDirectoryOnly)
                    .Where(p =>
                    {
                        try { return File.GetLastWriteTimeUtc(p) >= cutoff; }
                        catch { return false; }
                    })
                    .OrderByDescending(p =>
                    {
                        try { return File.GetLastWriteTimeUtc(p); }
                        catch { return DateTime.MinValue; }
                    })
                    .Take(3);

                foreach (var path in hits)
                {
                    AppendTail(sb, path);
                }
            }
            catch
            {
                // Best effort; skip unreadable directories.
            }
        }

        return sb.ToString();
    }

    private static void TryAddIfExists(List<string> list, string path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            {
                list.Add(path);
            }
        }
        catch { /* skip */ }
    }

    private static void AppendTail(System.Text.StringBuilder sb, string path)
    {
        try
        {
            const int maxLines = 25;
            string[] lines;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream))
            {
                lines = reader.ReadToEnd().Split('\n');
            }

            var tail = lines.Length > maxLines ? lines[^maxLines..] : lines;
            sb.AppendLine($"    -- {path} (mtime {File.GetLastWriteTime(path):yyyy-MM-dd HH:mm:ss}) --");
            foreach (var line in tail)
            {
                sb.Append("      ").AppendLine(line.TrimEnd('\r'));
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"    -- {path} (unreadable: {ex.Message}) --");
        }
    }

    private static string QuotePowerShellLiteral(string value) =>
        $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    internal string? ExtractZipAndLocateInstaller(string zipPath, string workDir, out string errorMessage)
    {
        errorMessage = string.Empty;
        var extractDir = Path.Combine(workDir, "extracted");

        try
        {
            Directory.CreateDirectory(extractDir);
            var fullExtractDir = Path.GetFullPath(extractDir);

            using (var archive = ZipFile.OpenRead(zipPath))
            {
                foreach (var entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name))
                    {
                        // Directory entry - skip.
                        continue;
                    }

                    var destinationPath = Path.GetFullPath(Path.Combine(extractDir, entry.FullName));
                    if (!destinationPath.StartsWith(fullExtractDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                        && !destinationPath.Equals(fullExtractDir, StringComparison.OrdinalIgnoreCase))
                    {
                        errorMessage = $"Zip archive rejected: entry '{entry.FullName}' escapes extraction directory.";
                        _logger.LogError("Zip Slip detected: entry {Entry} resolved to {Path}", entry.FullName, destinationPath);
                        return null;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                    entry.ExtractToFile(destinationPath, overwrite: true);
                }
            }
        }
        catch (Exception ex)
        {
            errorMessage = $"Could not extract zip: {ex.Message}";
            _logger.LogError(ex, "Zip extraction failed for {Zip}", zipPath);
            return null;
        }

        var located = LocateInstallerInTree(extractDir);
        if (located is null)
        {
            errorMessage = "Zip archive did not contain a recognised installer (Setup.exe / Install.exe / *.msi).";
            return null;
        }

        _logger.LogInformation("Zip extracted to {Dir}, selected installer {Installer}", extractDir, located);
        return located;
    }

    internal static string? LocateInstallerInTree(string root)
    {
        if (!Directory.Exists(root))
        {
            return null;
        }

        var candidates = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(p => new { Path = p, Depth = p.AsSpan(root.Length).Count(Path.DirectorySeparatorChar) + p.AsSpan(root.Length).Count(Path.AltDirectorySeparatorChar) })
            .ToArray();

        string? Match(string fileName) => candidates
            .Where(c => Path.GetFileName(c.Path).Equals(fileName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(c => c.Depth)
            .Select(c => c.Path)
            .FirstOrDefault();

        return Match("Setup.exe")
            ?? Match("Install.exe")
            ?? candidates
                .Where(c => Path.GetExtension(c.Path).Equals(".msi", StringComparison.OrdinalIgnoreCase))
                .OrderBy(c => c.Depth)
                .Select(c => c.Path)
                .FirstOrDefault()
            ?? candidates
                .Where(c => Path.GetExtension(c.Path).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                .OrderBy(c => c.Depth)
                .Select(c => c.Path)
                .FirstOrDefault();
    }

    internal static bool TryBuildVendorInstallerCommand(
        UpdateCandidate candidate,
        string installerPath,
        out string fileName,
        out string arguments,
        out string skipReason)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentException.ThrowIfNullOrWhiteSpace(installerPath);

        var extension = Path.GetExtension(installerPath);
        if (extension.Equals(".msi", StringComparison.OrdinalIgnoreCase))
        {
            fileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "msiexec.exe");
            arguments = $"/i \"{installerPath}\" /qn /norestart";
            skipReason = string.Empty;
            return true;
        }

        if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
            && TryGetKnownExeSilentArguments(candidate.SourceUpdateId, installerPath, out arguments))
        {
            fileName = installerPath;
            skipReason = string.Empty;
            return true;
        }

        fileName = string.Empty;
        arguments = string.Empty;
        skipReason = $"Vendor installer type '{extension}' is not approved for unattended install.";
        return false;
    }

    private static bool TryGetKnownExeSilentArguments(string sourceUpdateId, string installerPath, out string arguments)
    {
        arguments = string.Empty;

        if (!sourceUpdateId.StartsWith("vendor-installer:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (sourceUpdateId.StartsWith("vendor-installer:msi-wrapper:", StringComparison.OrdinalIgnoreCase))
        {
            arguments = $"/quiet /norestart /log \"{Path.ChangeExtension(installerPath, ".log")}\"";
            return true;
        }

        if (sourceUpdateId.StartsWith("vendor-installer:nullsoft:", StringComparison.OrdinalIgnoreCase))
        {
            arguments = "/S";
            return true;
        }

        // AMD's chipset bundle reports itself as NSIS but the inner installer is an
        // InstallAware wrapper that ignores /S and exits 2. Per AMD's own KB article
        // ("Silent Installation of AMD Chipset Drivers") the documented unattended
        // flag is -INSTALL.
        if (sourceUpdateId.StartsWith("vendor-installer:amd-chipset:", StringComparison.OrdinalIgnoreCase))
        {
            arguments = "-INSTALL";
            return true;
        }

        if (sourceUpdateId.StartsWith("vendor-installer:nvidia:", StringComparison.OrdinalIgnoreCase))
        {
            arguments = "-s -noeula -noreboot";
            return true;
        }

        if (sourceUpdateId.StartsWith("vendor-installer:inno:", StringComparison.OrdinalIgnoreCase))
        {
            arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART";
            return true;
        }

        if (sourceUpdateId.StartsWith("vendor-installer:installshield:", StringComparison.OrdinalIgnoreCase))
        {
            arguments = "/s";
            return true;
        }

        if (sourceUpdateId.StartsWith("vendor-installer:oem-tool:dell-command-update:", StringComparison.OrdinalIgnoreCase))
        {
            arguments = "/applyUpdates -silent -reboot=disable";
            return true;
        }

        if (sourceUpdateId.StartsWith("vendor-installer:oem-tool:lenovo-system-update:", StringComparison.OrdinalIgnoreCase))
        {
            arguments = "/CM -search A -action INSTALL -noicon -noreboot";
            return true;
        }

        if (sourceUpdateId.StartsWith("vendor-installer:oem-tool:hp-image-assistant:", StringComparison.OrdinalIgnoreCase))
        {
            var downloadFolder = Path.Combine(Path.GetTempPath(), "DriverUpdater", "HPImageAssistant");
            Directory.CreateDirectory(downloadFolder);
            arguments = $"/Operation:Analyze /Action:Install /Silent /Noninteractive /SoftpaqDownloadFolder:\"{downloadFolder}\"";
            return true;
        }

        return false;
    }

    internal static string BuildDryRunSummary(UpdateOperation operation, InstallOptions options)
    {
        var lines = new List<string>();
        if (options.CreateRestorePoint)
        {
            lines.Add($"1. Create system restore point: \"DriverUpdater - before {operation.TargetSnapshot.DeviceName}\"");
        }
        if (options.BackupCurrentDriver)
        {
            lines.Add($"{lines.Count + 1}. Back up current driver ({operation.TargetSnapshot.InfName ?? "INF unknown"}) to %ProgramData%\\DriverUpdater\\Backups");
        }
        lines.Add($"{lines.Count + 1}. Download from {operation.Candidate.Source} ({operation.Candidate.DownloadUrl})");
        lines.Add($"{lines.Count + 1}. Install version {operation.Candidate.NewVersion}, {operation.Candidate.SizeBytes:N0} bytes");
        return string.Join('\n', lines);
    }
}
