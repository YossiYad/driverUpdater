using DriverUpdater.Core.Models;

namespace DriverUpdater.App.ViewModels;

/// <summary>
/// Turns an AI lead that only names a web page into the package that actually installs it.
///
/// The AI answers per device, so it points a whole family of components at whatever page the
/// vendor publishes for them: eight SteelSeries devices at the GG download page, the AMD PSP,
/// audio and tooling components at an AMD landing page. None of those pages carry a direct
/// installer link, so every one of those rows used to end the run as "no safe in-app installer"
/// even when the very same scan had already found the real package from a deterministic source.
///
/// Vendors ship those components inside one package, which is exactly what the deterministic
/// source found, so the lead is retargeted onto it. The install then runs once and the pipeline
/// reports the rest as covered by a shared package.
/// </summary>
public static class AdvisoryLeadRetargeting
{
    public static bool IsAdvisoryPageLead(UpdateCandidate? candidate) =>
        candidate is { InstallKind: UpdateInstallKind.VendorPage }
        && candidate.SourceUpdateId.StartsWith("ai-latest:", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Picks the package a lead should install, out of the packages this scan already proved
    /// installable. Returns null when nothing in the scan covers the device.
    /// </summary>
    public static UpdateCandidate? FindPackageForLead(
        DriverInfo driver,
        UpdateCandidate lead,
        IReadOnlyList<UpdateCandidate> installablePackages)
    {
        ArgumentNullException.ThrowIfNull(driver);
        ArgumentNullException.ThrowIfNull(lead);
        ArgumentNullException.ThrowIfNull(installablePackages);

        var wanted = PackageTokenFor(driver, lead);
        if (wanted is null)
        {
            return null;
        }

        return installablePackages.FirstOrDefault(package =>
            package.InstallKind == UpdateInstallKind.VendorInstaller
            && package.SourceUpdateId.Contains(wanted, StringComparison.OrdinalIgnoreCase));
    }

    // Which vendor package covers this device. Keyed off the page the AI landed on plus the
    // device itself, because one vendor ships more than one package: AMD's chipset bundle and
    // its graphics driver both live under amd.com and cover different components.
    private static string? PackageTokenFor(DriverInfo driver, UpdateCandidate lead)
    {
        var host = lead.DownloadUrl.IsAbsoluteUri ? lead.DownloadUrl.Host : string.Empty;
        var vendor = driver.Provider + " " + driver.Manufacturer + " " + driver.DeviceName;

        if (Mentions(host, "amd.com") || Mentions(vendor, "Advanced Micro Devices"))
        {
            return driver.Category switch
            {
                DriverCategory.Display => "amd-radeon",
                DriverCategory.Audio => "amd-radeon",
                _ => "amd-chipset"
            };
        }

        if (Mentions(host, "steelseries") || Mentions(vendor, "SteelSeries"))
        {
            return "SteelSeries.GG";
        }

        if (Mentions(host, "logitech") || Mentions(host, "logi.com") || Mentions(vendor, "Logitech"))
        {
            return "Logitech.GHUB";
        }

        if (Mentions(host, "nvidia") || Mentions(vendor, "NVIDIA"))
        {
            return "nvidia";
        }

        if (Mentions(host, "intel") || Mentions(vendor, "Intel"))
        {
            return "intel-graphics";
        }

        return null;
    }

    /// <summary>
    /// True when the lead points at a Windows servicing article rather than a driver package.
    /// Those components are updated by Windows Update itself, so no installer exists to run and
    /// offering the row as a pending vendor page only ever produced a dead end.
    /// </summary>
    public static bool IsWindowsUpdateDelivered(UpdateCandidate lead)
    {
        ArgumentNullException.ThrowIfNull(lead);
        if (!lead.DownloadUrl.IsAbsoluteUri)
        {
            return false;
        }

        var host = lead.DownloadUrl.Host;
        return Mentions(host, "support.microsoft.com")
            && lead.DownloadUrl.AbsoluteUri.Contains("/topic/", StringComparison.OrdinalIgnoreCase);
    }

    public static UpdateCandidate Retarget(UpdateCandidate lead, UpdateCandidate package) =>
        lead with
        {
            DownloadUrl = package.DownloadUrl,
            SourceUpdateId = package.SourceUpdateId,
            InstallKind = package.InstallKind,
            SizeBytes = package.SizeBytes,
            RebootBehavior = package.RebootBehavior,
            VersionLabel = package.VersionLabel ?? lead.VersionLabel,
            Confidence = UpdateConfidence.Advisory
        };

    private static bool Mentions(string? value, string token) =>
        !string.IsNullOrWhiteSpace(value) && value.Contains(token, StringComparison.OrdinalIgnoreCase);
}
