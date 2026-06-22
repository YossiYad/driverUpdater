using Microsoft.Win32;
using System.Runtime.Versioning;

namespace DriverUpdater.Services.Sources.Internal;

internal sealed record InstalledPackage(
    string DisplayName,
    string? DisplayVersion,
    string? Publisher,
    string RegistryPath);

internal static class InstalledPackageInventory
{
    internal static IReadOnlyList<InstalledPackage> ReadAll()
    {
        if (!OperatingSystem.IsWindows())
        {
            return Array.Empty<InstalledPackage>();
        }

        var packages = new List<InstalledPackage>();
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (var view in GetRegistryViews())
            {
                ReadUninstallRoot(packages, hive, view);
            }
        }

        return packages
            .GroupBy(
                p => $"{p.DisplayName}\0{p.DisplayVersion}\0{p.Publisher}",
                StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToArray();
    }

    internal static string? FindHighestVersion(
        IEnumerable<InstalledPackage> packages,
        Func<InstalledPackage, bool> predicate)
    {
        return packages
            .Where(predicate)
            .Select(p => new { Package = p, Version = ParsePackageVersion(p.DisplayVersion) })
            .Where(x => x.Version is not null)
            .OrderByDescending(x => x.Version)
            .Select(x => x.Package.DisplayVersion)
            .FirstOrDefault();
    }

    internal static bool IsInstalledPackageCurrent(string? installedVersion, string releaseVersion)
    {
        var installed = ParsePackageVersion(installedVersion);
        var release = ParsePackageVersion(releaseVersion);
        return installed is not null && release is not null && installed >= release;
    }

    internal static Version? ParsePackageVersion(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var numeric = new string(raw.Trim()
            .TakeWhile(c => char.IsDigit(c) || c == '.')
            .ToArray())
            .TrimEnd('.');
        return Version.TryParse(numeric, out var version) ? version : null;
    }

    [SupportedOSPlatform("windows")]
    private static RegistryView[] GetRegistryViews() =>
        Environment.Is64BitOperatingSystem
            ? [RegistryView.Registry64, RegistryView.Registry32]
            : [RegistryView.Default];

    [SupportedOSPlatform("windows")]
    private static void ReadUninstallRoot(
        ICollection<InstalledPackage> packages,
        RegistryHive hive,
        RegistryView view)
    {
        const string uninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var root = baseKey.OpenSubKey(uninstallPath);
            if (root is null)
            {
                return;
            }

            foreach (var subKeyName in root.GetSubKeyNames())
            {
                try
                {
                    using var key = root.OpenSubKey(subKeyName);
                    var displayName = key?.GetValue("DisplayName")?.ToString();
                    if (string.IsNullOrWhiteSpace(displayName))
                    {
                        continue;
                    }

                    packages.Add(new InstalledPackage(
                        displayName.Trim(),
                        key?.GetValue("DisplayVersion")?.ToString()?.Trim(),
                        key?.GetValue("Publisher")?.ToString()?.Trim(),
                        $"{hive}/{view}/{uninstallPath}/{subKeyName}"));
                }
                catch (Exception)
                {
                    // One malformed or inaccessible product key must not hide the rest.
                }
            }
        }
        catch (Exception)
        {
            // Registry access may be restricted by policy; callers fall back to driver data.
        }
    }
}

internal static class AmdInstalledPackageDetector
{
    internal static string? GetRadeonPackageVersion() =>
        FindRadeonPackageVersion(InstalledPackageInventory.ReadAll());

    internal static string? GetChipsetPackageVersion() =>
        FindChipsetPackageVersion(InstalledPackageInventory.ReadAll());

    internal static string? FindRadeonPackageVersion(IEnumerable<InstalledPackage> packages) =>
        InstalledPackageInventory.FindHighestVersion(packages, package =>
            IsAmdPublisher(package.Publisher)
            && (Contains(package.DisplayName, "AMD Software")
                || Contains(package.DisplayName, "Radeon Software"))
            && !Contains(package.DisplayName, "Chipset"));

    internal static string? FindChipsetPackageVersion(IEnumerable<InstalledPackage> packages) =>
        InstalledPackageInventory.FindHighestVersion(packages, package =>
            IsAmdPublisher(package.Publisher)
            && Contains(package.DisplayName, "AMD")
            && Contains(package.DisplayName, "Chipset"));

    private static bool IsAmdPublisher(string? publisher) =>
        string.IsNullOrWhiteSpace(publisher)
        || Contains(publisher, "Advanced Micro Devices")
        || string.Equals(publisher.Trim(), "AMD", StringComparison.OrdinalIgnoreCase);

    private static bool Contains(string? value, string needle) =>
        value?.Contains(needle, StringComparison.OrdinalIgnoreCase) == true;
}
