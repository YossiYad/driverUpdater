using DriverUpdater.Core.Models;

namespace DriverUpdater.App.ViewModels;

/// <summary>
/// Decides whether a page the AI named may become a download lead at all.
///
/// Asked for "the official download page", the model has handed back third-party download
/// portals and driver-aggregator sites - the exact places a driver updater must never fetch a
/// binary from. The page only qualifies when its registrable domain is one the vendor itself
/// publishes on: either a known vendor domain, or a domain whose name is the vendor's own name
/// as the device reports it.
///
/// Matching is on the registrable domain rather than a substring of the host, so a look-alike
/// like "amd-driver-download.com" does not pass for containing "amd".
/// </summary>
public static class AiDiscoverySourceTrust
{
    private static readonly string[] OfficialDomains =
    [
        "microsoft.com",
        "windowsupdate.com",
        "amd.com",
        "nvidia.com",
        "geforce.com",
        "intel.com",
        "realtek.com",
        "logitech.com",
        "logi.com",
        "steelseries.com",
        "corsair.com",
        "razer.com",
        "gigabyte.com",
        "asus.com",
        "msi.com",
        "asrock.com",
        "biostar.com.tw",
        "dell.com",
        "hp.com",
        "hpcloud.hp.com",
        "lenovo.com",
        "acer.com",
        "samsung.com",
        "broadcom.com",
        "qualcomm.com",
        "mediatek.com",
        "synaptics.com",
        "elantech.com",
        "conexant.com",
        "creative.com",
        "seagate.com",
        "westerndigital.com",
        "crucial.com",
        "kingston.com",
        "sandisk.com",
        "tailscale.com",
        "wintun.net",
        "github.com"
    ];

    public static bool IsPublishedByTheVendor(DriverInfo device, Uri url)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(url);

        if (!url.IsAbsoluteUri || url.Scheme is not ("http" or "https"))
        {
            return false;
        }

        var registrable = RegistrableDomain(url.Host);
        if (registrable.Length == 0)
        {
            return false;
        }

        if (OfficialDomains.Any(domain =>
                registrable.Equals(domain, StringComparison.OrdinalIgnoreCase)
                || registrable.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var name = registrable.Split('.')[0];
        return VendorNames(device).Any(vendor => name.Equals(vendor, StringComparison.OrdinalIgnoreCase));
    }

    // "Advanced Micro Devices, Inc." carries no usable domain name, while "Realtek" and
    // "SteelSeries" are exactly what those vendors register. Short and generic words are
    // dropped so a device made by "Standard system devices" cannot vouch for a domain.
    private static IEnumerable<string> VendorNames(DriverInfo device)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in new[] { device.Provider, device.Manufacturer })
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                continue;
            }

            foreach (var word in source.Split([' ', ',', '.', '(', ')', '-', '/'], StringSplitOptions.RemoveEmptyEntries))
            {
                var normalized = new string(word.Where(char.IsLetterOrDigit).ToArray());
                if (normalized.Length >= 5 && !GenericWords.Contains(normalized) && seen.Add(normalized))
                {
                    yield return normalized;
                }
            }
        }
    }

    private static readonly HashSet<string> GenericWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "standard",
        "system",
        "systems",
        "devices",
        "device",
        "generic",
        "advanced",
        "micro",
        "technology",
        "technologies",
        "electronics",
        "software",
        "solutions",
        "corporation",
        "company",
        "limited",
        "incorporated",
        "international"
    };

    private static string RegistrableDomain(string host)
    {
        var labels = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return labels.Length < 2
            ? string.Empty
            : string.Join('.', labels[^2..]);
    }
}
