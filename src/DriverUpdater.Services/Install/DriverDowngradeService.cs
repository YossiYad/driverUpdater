using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using DriverUpdater.Core.Results;
using Microsoft.Extensions.Logging;

namespace DriverUpdater.Services.Install;

/// <summary>
/// Downgrades a device to an older driver version whose package is still present in the Windows
/// DriverStore. Windows always binds the highest-ranked package, so the way "back" is to remove
/// the currently bound (newer) package - after exporting it as a backup - and let a device
/// rescan re-bind the best remaining package, which is the older one. The result is verified
/// against the device's actually bound driver, not the tool's exit codes.
/// </summary>
public sealed class DriverDowngradeService : IDriverDowngradeService
{
    private readonly IDriverStoreBrowser _driverStore;
    private readonly IBackupService _backup;
    private readonly IPnPUtilRunner _pnputil;
    private readonly IInstalledDriverProbe _probe;
    private readonly ILogger<DriverDowngradeService> _logger;

    public DriverDowngradeService(
        IDriverStoreBrowser driverStore,
        IBackupService backup,
        IPnPUtilRunner pnputil,
        IInstalledDriverProbe probe,
        ILogger<DriverDowngradeService> logger)
    {
        ArgumentNullException.ThrowIfNull(driverStore);
        ArgumentNullException.ThrowIfNull(backup);
        ArgumentNullException.ThrowIfNull(pnputil);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(logger);
        _driverStore = driverStore;
        _backup = backup;
        _pnputil = pnputil;
        _probe = probe;
        _logger = logger;
    }

    public async Task<Result<DriverDowngradeOutcome>> DowngradeAsync(
        DriverInfo driver,
        DriverVersionRecord target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(driver);
        ArgumentNullException.ThrowIfNull(target);

        if (string.IsNullOrWhiteSpace(driver.InfName))
        {
            return ResultError.From(
                "DOWNGRADE_NO_CURRENT_INF",
                $"'{driver.DeviceName}' has no current INF name, so the bound package cannot be removed.");
        }
        if (string.Equals(driver.InfName, target.InfName, StringComparison.OrdinalIgnoreCase))
        {
            return ResultError.From(
                "DOWNGRADE_SAME_PACKAGE",
                "The selected version uses the same driver package as the current one; there is nothing to remove.");
        }
        if (!driver.InfName.StartsWith("oem", StringComparison.OrdinalIgnoreCase))
        {
            return ResultError.From(
                "DOWNGRADE_INBOX_DRIVER",
                $"'{driver.DeviceName}' is using the Windows inbox driver '{driver.InfName}', which cannot be removed.");
        }

        // The older package must still be in the DriverStore; only then is the downgrade free.
        var packages = await _driverStore.EnumeratePackagesAsync(cancellationToken).ConfigureAwait(false);
        var targetPresent = !string.IsNullOrWhiteSpace(target.InfName)
            && packages.Any(p => string.Equals(p.PublishedName, target.InfName, StringComparison.OrdinalIgnoreCase));
        if (!targetPresent)
        {
            return ResultError.From(
                "DOWNGRADE_TARGET_MISSING",
                $"Version {target.Version} is no longer in the Windows driver store; it must be reinstalled from the vendor instead.");
        }

        _logger.LogInformation(
            "Downgrade: {Device} from {CurrentVersion} ({CurrentInf}) to {TargetVersion} ({TargetInf})",
            driver.DeviceName, driver.CurrentVersion, driver.InfName, target.Version, target.InfName);

        // Safety net: export the newer package before deleting it, so the user can come back.
        var backup = await _backup.BackupDriverAsync(driver, cancellationToken).ConfigureAwait(false);
        if (!backup.IsSuccess)
        {
            return ResultError.From(
                "DOWNGRADE_BACKUP_FAILED",
                $"Could not back up the current driver before removal: {backup.Error.Message}");
        }

        var delete = await _pnputil
            .RunAsync($"/delete-driver \"{driver.InfName}\" /uninstall /force", cancellationToken)
            .ConfigureAwait(false);
        if (!delete.IsSuccess)
        {
            _logger.LogError(
                "Downgrade: pnputil delete-driver {Inf} failed with exit {Code}: {Error}",
                driver.InfName, delete.ExitCode, delete.StandardError.Trim());
            return ResultError.From(
                "DOWNGRADE_DELETE_FAILED",
                $"pnputil delete-driver exit {delete.ExitCode}: {delete.StandardError.Trim()}");
        }

        var rescan = await _pnputil.RunAsync("/scan-devices", cancellationToken).ConfigureAwait(false);
        if (!rescan.IsSuccess)
        {
            _logger.LogWarning(
                "Downgrade: pnputil scan-devices exit {Code} after removing {Inf}; Windows may still rebind on its own",
                rescan.ExitCode, driver.InfName);
        }

        var state = await _probe.GetCurrentAsync(driver.DeviceId, cancellationToken).ConfigureAwait(false);
        var boundVersion = state?.Version?.ToString();
        var verified = string.Equals(boundVersion, target.Version, StringComparison.OrdinalIgnoreCase);

        _logger.LogInformation(
            "Downgrade: {Device} now reports version {BoundVersion} (target {TargetVersion}, verified={Verified})",
            driver.DeviceName, boundVersion ?? "(unknown)", target.Version, verified);

        return new DriverDowngradeOutcome(
            DeviceId: driver.DeviceId,
            TargetVersion: target.Version,
            BoundVersionAfter: boundVersion,
            VerifiedDowngraded: verified,
            BackupFolderPath: backup.Value.BackupFolderPath);
    }
}
