using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Security;
using System.Text.RegularExpressions;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using Microsoft.Win32;
using Microsoft.Extensions.Logging;

namespace DriverUpdater.Services.Sources;

public sealed partial class WingetVendorToolSource : IUpdateSource
{
    private static readonly VendorToolPackage[] Packages =
    [
        new(
            "SteelSeries.GG",
            "SteelSeries GG",
            driver => Contains(driver.HardwareId, "VID_1038")
                || Contains(driver.Provider, "SteelSeries")
                || Contains(driver.Manufacturer, "SteelSeries")
                || Contains(driver.DeviceName, "SteelSeries"),
            driver => Contains(driver.DeviceName, "SteelSeries GG Component Device") ? 100 : 0),
        new(
            "Logitech.GHUB",
            "Logitech G HUB",
            driver => Contains(driver.DeviceName, "LIGHTSPEED")
                || Contains(driver.DeviceName, "Logitech G HUB")
                || Contains(driver.DeviceName, "Logi G"),
            driver => Contains(driver.DeviceName, "Logitech G HUB Virtual Bus Enumerator") ? 100
                : Contains(driver.DeviceName, "LIGHTSPEED") ? 50
                : 0),
        new(
            "Tailscale.Tailscale",
            "Tailscale",
            driver => Contains(driver.DeviceName, "Tailscale")
                || Contains(driver.Provider, "Tailscale")
                || Contains(driver.Manufacturer, "Tailscale"),
            driver => Contains(driver.DeviceName, "Tailscale Tunnel") ? 100 : 0),
        new(
            "ViGEm.ViGEmBus",
            "ViGEm Bus Driver",
            driver => Contains(driver.DeviceName, "ViGEm")
                || Contains(driver.DeviceName, "Nefarius Virtual Gamepad")
                || Contains(driver.Provider, "Nefarius")
                || Contains(driver.Manufacturer, "Nefarius"),
            driver => Contains(driver.DeviceName, "ViGEm Bus")
                || Contains(driver.DeviceName, "Virtual Gamepad Emulation Bus") ? 100 : 0)
    ];

    private readonly IVendorInstallerRunner _runner;
    private readonly IFileSignatureVerifier _signatureVerifier;
    private readonly ILogger<WingetVendorToolSource> _logger;
    private readonly TimeProvider _clock;
    private readonly Func<string?> _wingetLocator;

    public WingetVendorToolSource(
        IVendorInstallerRunner runner,
        IFileSignatureVerifier signatureVerifier,
        ILogger<WingetVendorToolSource> logger,
        TimeProvider? clock = null,
        Func<string?>? wingetLocator = null)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(signatureVerifier);
        ArgumentNullException.ThrowIfNull(logger);
        _runner = runner;
        _signatureVerifier = signatureVerifier;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        _wingetLocator = wingetLocator ?? FindWingetExecutable;
    }

    public UpdateSource Kind => UpdateSource.Oem;

    public string DisplayName => "Official vendor tools via WinGet";

    public async IAsyncEnumerable<UpdateCandidate> SearchAsync(
        IReadOnlyCollection<DriverInfo> drivers,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(drivers);

        var matchedPackages = Packages
            .Select(package => (Package: package, Drivers: drivers.Where(package.Matches).ToArray()))
            .Where(item => item.Drivers.Length > 0)
            .ToArray();
        if (matchedPackages.Length == 0)
        {
            yield break;
        }

        var wingetPath = _wingetLocator();
        if (string.IsNullOrWhiteSpace(wingetPath) || !File.Exists(wingetPath))
        {
            _logger.LogInformation("WinGet vendor tools skipped: a real winget.exe package binary was not found");
            yield break;
        }

        var signature = _signatureVerifier.Verify(wingetPath);
        if (!signature.IsTrusted
            || !Contains(signature.Publisher ?? string.Empty, "Microsoft Corporation"))
        {
            _logger.LogWarning(
                "WinGet vendor tools rejected {Path}: trusted={Trusted}, publisher={Publisher}, reason={Reason}",
                wingetPath,
                signature.IsTrusted,
                signature.Publisher ?? "<missing>",
                signature.ErrorMessage ?? "unexpected publisher");
            yield break;
        }

        foreach (var (package, matchedDrivers) in matchedPackages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = await QueryPackageAsync(wingetPath, package, cancellationToken).ConfigureAwait(false);
            if (state is null)
            {
                continue;
            }

            var sourceUpdateId = $"vendor-installer:winget:{state.Mode}:{package.Id}:{state.AvailableVersionText}";
            var date = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime.Date);
            var driver = matchedDrivers
                .OrderByDescending(package.RepresentativeScore)
                .ThenBy(item => item.DeviceName, StringComparer.OrdinalIgnoreCase)
                .First();
            yield return new UpdateCandidate(
                ForHardwareId: driver.HardwareId,
                Source: UpdateSource.Oem,
                NewVersion: state.AvailableVersion,
                NewDate: date,
                DownloadUrl: new Uri(wingetPath),
                SizeBytes: 0,
                KbArticle: null,
                IsSuperseded: false,
                SourceUpdateId: sourceUpdateId,
                SupersededIds: Array.Empty<string>(),
                InstallKind: UpdateInstallKind.VendorInstaller,
                Confidence: UpdateConfidence.Confirmed,
                VersionLabel: $"{package.DisplayName} {state.AvailableVersionText}",
                InstalledVersionLabel: state.InstalledVersionText is null
                    ? null
                    : $"{package.DisplayName} {state.InstalledVersionText}");
        }
    }

    private async Task<WingetPackageUpdate?> QueryPackageAsync(
        string wingetPath,
        VendorToolPackage package,
        CancellationToken cancellationToken)
    {
        var common = $"--id \"{package.Id}\" --exact --source winget --accept-source-agreements --disable-interactivity";
        var listed = await _runner.RunAsync(wingetPath, $"list {common}", cancellationToken).ConfigureAwait(false);
        if (TryParsePackageRow(listed.StandardOutput, package.Id, out var installed, out var available))
        {
            if (available is null)
            {
                _logger.LogInformation(
                    "WinGet reports {Package} is current at {Version}",
                    package.Id,
                    installed ?? "unknown");
                return null;
            }

            _logger.LogInformation(
                "WinGet reports an update for {Package}: installed={Installed}, available={Available}",
                package.Id,
                installed ?? "unknown",
                available);
            return TryBuildPackageUpdate("upgrade", available, installed);
        }

        var searched = await _runner.RunAsync(wingetPath, $"search {common}", cancellationToken).ConfigureAwait(false);
        if (!TryParsePackageRow(searched.StandardOutput, package.Id, out var foundVersion, out var foundAvailable))
        {
            _logger.LogWarning(
                "WinGet could not resolve package {Package}, listExit={ListExit}, searchExit={SearchExit}",
                package.Id,
                listed.ExitCode,
                searched.ExitCode);
            return null;
        }

        var latest = foundAvailable ?? foundVersion;
        if (latest is null)
        {
            return null;
        }

        _logger.LogInformation(
            "WinGet vendor tool {Package} is not installed; version {Version} can cover the matching device family",
            package.Id,
            latest);
        return TryBuildPackageUpdate("install", latest, installedVersionText: null);
    }

    private static WingetPackageUpdate? TryBuildPackageUpdate(
        string mode,
        string versionText,
        string? installedVersionText)
    {
        var versionMatch = VersionPattern().Match(versionText);
        if (!versionMatch.Success || !Version.TryParse(versionMatch.Value, out var version))
        {
            return null;
        }

        return new WingetPackageUpdate(mode, versionText, version, installedVersionText);
    }

    internal static bool TryParsePackageRow(
        string output,
        string packageId,
        out string? installedVersion,
        out string? availableVersion)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);

        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var idIndex = line.IndexOf(packageId, StringComparison.OrdinalIgnoreCase);
            if (idIndex < 0)
            {
                continue;
            }

            var afterId = line[(idIndex + packageId.Length)..];
            var versions = VersionPattern().Matches(afterId)
                .Select(match => match.Value)
                .ToArray();
            var installedUnknown = afterId.Contains("<unknown>", StringComparison.OrdinalIgnoreCase)
                || afterId.Contains("Unknown", StringComparison.OrdinalIgnoreCase);

            if (installedUnknown && versions.Length >= 1)
            {
                installedVersion = null;
                availableVersion = versions[0];
                return true;
            }
            if (versions.Length >= 2)
            {
                installedVersion = versions[0];
                availableVersion = versions[1];
                return true;
            }
            if (versions.Length == 1)
            {
                installedVersion = versions[0];
                availableVersion = null;
                return true;
            }
        }

        installedVersion = null;
        availableVersion = null;
        return false;
    }

    internal static string? FindWingetExecutable()
    {
        try
        {
            var registeredPath = OperatingSystem.IsWindows()
                ? FindRegisteredWingetExecutable()
                : null;
            if (registeredPath is not null)
            {
                return registeredPath;
            }

            var windowsApps = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "WindowsApps");
            if (!Directory.Exists(windowsApps))
            {
                return null;
            }

            return Directory
                .EnumerateDirectories(windowsApps, "Microsoft.DesktopAppInstaller_*__8wekyb3d8bbwe")
                .Select(directory => Path.Combine(directory, "winget.exe"))
                .Where(File.Exists)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static string? FindRegisteredWingetExecutable()
    {
        const string repositoryKey =
            @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";
        using var packages = Registry.CurrentUser.OpenSubKey(repositoryKey);
        if (packages is null)
        {
            return null;
        }

        return packages.GetSubKeyNames()
            .Where(name => name.StartsWith("Microsoft.DesktopAppInstaller_", StringComparison.OrdinalIgnoreCase)
                && name.EndsWith("__8wekyb3d8bbwe", StringComparison.OrdinalIgnoreCase))
            .Select(name =>
            {
                using var package = packages.OpenSubKey(name);
                var root = package?.GetValue("PackageRootFolder") as string;
                return string.IsNullOrWhiteSpace(root) ? null : Path.Combine(root, "winget.exe");
            })
            .Where(path => path is not null && File.Exists(path))
            .OrderByDescending(path => File.GetLastWriteTimeUtc(path!))
            .FirstOrDefault();
    }

    private static bool Contains(string value, string fragment) =>
        value.Contains(fragment, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"(?<![\d.])\d+(?:\.\d+){1,3}(?![\d.])")]
    private static partial Regex VersionPattern();

    private sealed record VendorToolPackage(
        string Id,
        string DisplayName,
        Func<DriverInfo, bool> Matches,
        Func<DriverInfo, int> RepresentativeScore);

    private sealed record WingetPackageUpdate(
        string Mode,
        string AvailableVersionText,
        Version AvailableVersion,
        string? InstalledVersionText);
}
