using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using Microsoft.Extensions.Logging;

namespace DriverUpdater.Infrastructure.PnPUtil;

/// <summary>
/// Completes an install that only reached the driver store. "pnputil /add-driver /install"
/// installs a package only when Windows ranks it above the bound driver, so a newer vendor
/// package can sit staged while the device keeps running the old one. UpdateDriverForPlugAndPlayDevices
/// takes INSTALLFLAG_FORCE, which is the documented way to complete that switch.
///
/// Only ever used to move a device forward: the staged package must report a strictly higher
/// version than what is bound, so the force flag cannot be used to push an older driver on.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class SetupApiForceDriverBinder : IForceDriverBinder
{
    private const uint InstallflagForce = 0x00000001;
    private const uint InstallflagNonInteractive = 0x00000004;

    private readonly IPnPUtilRunner _pnputil;
    private readonly ILogger<SetupApiForceDriverBinder> _logger;

    public SetupApiForceDriverBinder(IPnPUtilRunner pnputil, ILogger<SetupApiForceDriverBinder> logger)
    {
        ArgumentNullException.ThrowIfNull(pnputil);
        ArgumentNullException.ThrowIfNull(logger);
        _pnputil = pnputil;
        _logger = logger;
    }

    public async Task<ForceBindResult> TryBindStagedDriverAsync(
        DriverInfo device,
        Version? atLeastVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);

        if (string.IsNullOrWhiteSpace(device.HardwareId))
        {
            return new ForceBindResult(false, false, false, null, null, "The device reports no hardware ID.");
        }

        var enumerate = await _pnputil
            .RunAsync($"/enum-devices /instanceid \"{device.DeviceId}\" /drivers", cancellationToken)
            .ConfigureAwait(false);
        if (enumerate.ExitCode != 0)
        {
            return new ForceBindResult(
                false, false, false, null, null,
                $"Could not list the drivers staged for this device (pnputil exit {enumerate.ExitCode}).");
        }

        var staged = ParseStagedDrivers(enumerate.StandardOutput)
            .Where(entry => entry.Version is not null)
            .Where(entry => atLeastVersion is null || entry.Version >= atLeastVersion)
            .Where(entry => device.CurrentVersion is null || entry.Version > device.CurrentVersion)
            .OrderByDescending(entry => entry.Version)
            .FirstOrDefault();
        if (staged is null)
        {
            return new ForceBindResult(
                false, false, false, null, null,
                "Nothing newer than the bound driver is staged for this device.");
        }

        var infPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "INF",
            staged.InfName);

        _logger.LogInformation(
            "Force-binding {Inf} ({Version}) to {Device}: Windows staged it but kept {Installed}",
            staged.InfName,
            staged.Version,
            device.DeviceName,
            device.CurrentVersion?.ToString() ?? "the previous driver");

        try
        {
            var ok = UpdateDriverForPlugAndPlayDevicesW(
                IntPtr.Zero,
                device.HardwareId,
                infPath,
                InstallflagForce | InstallflagNonInteractive,
                out var rebootRequired);
            if (!ok)
            {
                var error = Marshal.GetLastWin32Error();
                _logger.LogWarning(
                    "Force bind of {Inf} to {Device} failed with Win32 error {Error}",
                    staged.InfName, device.DeviceName, error);
                return new ForceBindResult(
                    true, false, false, staged.InfName, staged.Version,
                    $"Windows refused to bind the staged driver (error {error}).");
            }

            return new ForceBindResult(true, true, rebootRequired, staged.InfName, staged.Version, null);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _logger.LogWarning(ex, "SetupAPI force bind is unavailable on this system");
            return new ForceBindResult(false, false, false, staged.InfName, staged.Version, ex.Message);
        }
    }

    // pnputil prints one block per matching driver, e.g.
    //   Driver Name:   oem42.inf
    //   Driver Version: 1.22.0.0
    //   Driver Rank:   0xFF0000
    internal static IReadOnlyList<StagedDriver> ParseStagedDrivers(string? output)
    {
        var results = new List<StagedDriver>();
        if (string.IsNullOrWhiteSpace(output))
        {
            return results;
        }

        string? infName = null;
        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim();
            var nameMatch = DriverNamePattern().Match(line);
            if (nameMatch.Success)
            {
                infName = nameMatch.Groups["inf"].Value.Trim();
                continue;
            }

            var versionMatch = DriverVersionPattern().Match(line);
            if (!versionMatch.Success || infName is null)
            {
                continue;
            }

            // pnputil prints "MM/dd/yyyy 1.2.3.4"; the version is the trailing token.
            var token = versionMatch.Groups["version"].Value.Trim().Split(' ').LastOrDefault();
            results.Add(new StagedDriver(
                infName,
                Version.TryParse(token, out var version) ? version : null));
            infName = null;
        }

        return results;
    }

    internal sealed record StagedDriver(string InfName, Version? Version);

    [DllImport("newdev.dll", EntryPoint = "UpdateDriverForPlugAndPlayDevicesW",
        CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateDriverForPlugAndPlayDevicesW(
        IntPtr hwndParent,
        string hardwareId,
        string fullInfPath,
        uint installFlags,
        [MarshalAs(UnmanagedType.Bool)] out bool rebootRequired);

    [GeneratedRegex(@"^Driver Name:\s*(?<inf>\S+\.inf)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex DriverNamePattern();

    [GeneratedRegex(@"^Driver Version:\s*(?<version>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex DriverVersionPattern();
}
