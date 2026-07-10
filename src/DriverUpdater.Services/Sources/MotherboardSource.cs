using System.Runtime.CompilerServices;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using DriverUpdater.Services.Sources.Internal.Motherboard;
using Microsoft.Extensions.Logging;

namespace DriverUpdater.Services.Sources;

public sealed class MotherboardSource : IUpdateSource
{
    private readonly IOemDetectionService _oem;
    private readonly IReadOnlyDictionary<OemVendor, IMotherboardScraper> _scrapers;
    private readonly ILogger<MotherboardSource> _logger;

    public MotherboardSource(
        IOemDetectionService oem,
        IReadOnlyDictionary<OemVendor, IMotherboardScraper> scrapers,
        ILogger<MotherboardSource> logger)
    {
        ArgumentNullException.ThrowIfNull(oem);
        ArgumentNullException.ThrowIfNull(scrapers);
        ArgumentNullException.ThrowIfNull(logger);
        _oem = oem;
        _scrapers = scrapers;
        _logger = logger;
    }

    public UpdateSource Kind => UpdateSource.Oem;

    public string DisplayName => "Motherboard vendor drivers";

    public async IAsyncEnumerable<UpdateCandidate> SearchAsync(
        IReadOnlyCollection<DriverInfo> drivers,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(drivers);

        var oemInfo = await _oem.DetectAsync(cancellationToken).ConfigureAwait(false);
        if (oemInfo is null)
        {
            _logger.LogInformation("Motherboard source skipped: no OEM detected");
            yield break;
        }

        if (!_scrapers.TryGetValue(oemInfo.Vendor, out var scraper))
        {
            _logger.LogInformation(
                "Motherboard source skipped: no scraper registered for vendor {Vendor}. Supported vendors: {Supported}",
                oemInfo.Vendor, string.Join(", ", _scrapers.Keys));
            yield break;
        }

        if (string.IsNullOrWhiteSpace(oemInfo.Model))
        {
            _logger.LogInformation("Motherboard source skipped: empty motherboard model for {Vendor}", oemInfo.Vendor);
            yield break;
        }

        _logger.LogInformation("Motherboard source: dispatching to scraper for {Vendor} ({Model})", oemInfo.Vendor, oemInfo.Model);

        IReadOnlyList<MotherboardDriverEntry> entries;
        try
        {
            entries = await scraper.GetDriversAsync(oemInfo.Model, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Vendor} scraper failed for {Model}", oemInfo.Vendor, oemInfo.Model);
            yield break;
        }

        if (entries.Count == 0)
        {
            _logger.LogInformation("{Vendor} scraper returned 0 entries for {Model}", oemInfo.Vendor, oemInfo.Model);
            yield break;
        }

        var vendorTag = oemInfo.Vendor.ToString().ToLowerInvariant();
        _logger.LogInformation("{Vendor}: matching {Count} catalog entries against {DriverCount} installed drivers",
            oemInfo.Vendor, entries.Count, drivers.Count);
        foreach (var entry in entries)
        {
            _logger.LogInformation(
                "{Vendor} catalog entry: title={Title} version={Version} date={Date} category={Category} url={Url}",
                oemInfo.Vendor, entry.Title, entry.Version, entry.ReleaseDate, entry.Category, entry.DownloadUrl);
        }

        var matched = 0;
        foreach (var driver in drivers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var match = FindMatch(driver, entries);
            if (match is null)
            {
                continue;
            }

            matched++;

            // Prefer comparing by version. The catalog version (entry.Version, e.g.
            // "6.0.9927.1") is the same Realtek/Windows driver version reported by WMI,
            // so a direct compare is meaningful. Comparing by date instead re-offers the
            // same driver forever, because Gigabyte's publish date is always later than
            // the installed INF date even when the version is identical. Fall back to the
            // date only when one side has no parseable version.
            var catalogVersion = TryParseEntryVersion(match.Version);
            if (catalogVersion is not null
                && driver.CurrentVersion is not null
                && AreComparableDriverVersions(driver.CurrentVersion, catalogVersion))
            {
                if (catalogVersion <= driver.CurrentVersion)
                {
                    _logger.LogInformation(
                        "{Vendor}: skipping {Device} - installed version {Installed} is at or newer than catalog version {Catalog}",
                        oemInfo.Vendor, driver.DeviceName, driver.CurrentVersion, catalogVersion);
                    continue;
                }
            }
            else if (driver.CurrentDate is { } currentDate && match.ReleaseDate <= currentDate)
            {
                _logger.LogInformation(
                    "{Vendor}: skipping {Device} - local driver date {LocalDate} is at or newer than catalog {RemoteDate} (no comparable version)",
                    oemInfo.Vendor, driver.DeviceName, currentDate, match.ReleaseDate);
                continue;
            }

            var candidate = BuildCandidate(driver, match, vendorTag, oemInfo.Model);
            _logger.LogInformation(
                "{Vendor}: yielding {InstallKind} candidate for {Device} -> {Url}",
                oemInfo.Vendor, candidate.InstallKind, driver.DeviceName, candidate.DownloadUrl);
            yield return candidate;
        }

        if (matched == 0)
        {
            _logger.LogInformation("{Vendor}: 0 of {Count} installed drivers matched any catalog entry by category heuristic",
                oemInfo.Vendor, drivers.Count);
        }
    }

    internal static UpdateCandidate BuildCandidate(DriverInfo driver, MotherboardDriverEntry entry, string vendorTag, string model) =>
        new(
            ForHardwareId: driver.HardwareId,
            Source: UpdateSource.Oem,
            NewVersion: BuildCandidateVersion(driver, entry),
            NewDate: entry.ReleaseDate,
            DownloadUrl: entry.DownloadUrl,
            SizeBytes: entry.SizeBytes ?? 0,
            KbArticle: null,
            IsSuperseded: false,
            SourceUpdateId: $"vendor-installer:{ResolveInstallerFamily(vendorTag)}:{vendorTag}:{model}:{entry.Title}:{entry.Version}",
            SupersededIds: Array.Empty<string>(),
            InstallKind: UpdateInstallKind.VendorInstaller);

    private static Version BuildCandidateVersion(DriverInfo driver, MotherboardDriverEntry entry)
    {
        var catalogVersion = TryParseEntryVersion(entry.Version);
        if (catalogVersion is null)
        {
            return DateToVersion(entry.ReleaseDate);
        }

        return driver.CurrentVersion is not null && !AreComparableDriverVersions(driver.CurrentVersion, catalogVersion)
            ? DateToVersion(entry.ReleaseDate)
            : catalogVersion;
    }

    internal static string ResolveInstallerFamily(string vendorTag) => vendorTag.ToLowerInvariant() switch
    {
        "gigabyte" => "nullsoft",
        "asus" => "nullsoft",
        _ => "installshield"
    };

    internal static MotherboardDriverEntry? FindMatch(DriverInfo driver, IReadOnlyList<MotherboardDriverEntry> entries)
    {
        // Skip virtual / peripheral devices that share a category with the real host
        // driver but are not actually managed by the vendor installer.
        if (Contains(driver.DeviceName, "HID")
            || Contains(driver.DeviceName, "Personal Area Network")
            || Contains(driver.DeviceName, "Enumerator")
            || Contains(driver.DeviceName, "Identification Service")
            || Contains(driver.DeviceName, "Streaming Service")
            || Contains(driver.DeviceName, "Audio Endpoint"))
        {
            return null;
        }

        var deviceName = driver.DeviceName;

        // Audio: only Realtek-branded audio chips. The AMD/Intel GPU HDMI audio devices
        // are handled by their respective GPU sources, not the motherboard installer.
        if (driver.Category == DriverCategory.Audio
            && (Contains(driver.Provider, "Realtek") || Contains(deviceName, "Realtek")))
        {
            var audio = BestByVersion(entries, e =>
                (Contains(e.Category, "Audio") || Contains(e.Title, "Audio"))
                && Contains(e.Title, "Realtek")
                && !Contains(e.Title, "LE Audio"));
            if (audio is not null) { return audio; }
        }

        if (driver.Category == DriverCategory.Network
            && (Contains(driver.Provider, "Realtek") || Contains(deviceName, "Realtek"))
            && (Contains(deviceName, "Ethernet") || Contains(deviceName, "GbE") || Contains(deviceName, "LAN")))
        {
            var lan = BestByVersion(entries, e => Contains(e.Category, "LAN") || Contains(e.Title, "LAN"));
            if (lan is not null) { return lan; }
        }

        if (driver.Category == DriverCategory.Bluetooth
            && (Contains(driver.Provider, "Realtek") || Contains(deviceName, "Realtek")))
        {
            var bt = BestByVersion(entries, e => Contains(e.Category, "Bluetooth") || Contains(e.Title, "Bluetooth"));
            if (bt is not null) { return bt; }
        }

        if (driver.Category == DriverCategory.Network
            && (Contains(driver.Provider, "Realtek") || Contains(deviceName, "Realtek"))
            && (Contains(deviceName, "Wireless") || Contains(deviceName, "Wi-Fi") || Contains(deviceName, "WiFi")))
        {
            var wifi = BestByVersion(entries, e => Contains(e.Category, "Wireless") || Contains(e.Title, "WiFi") || Contains(e.Title, "Wireless"));
            if (wifi is not null) { return wifi; }
        }

        // Intel chipset INF driver
        if (driver.Category is DriverCategory.Chipset or DriverCategory.System
            && (Contains(driver.Provider, "Intel") || Contains(deviceName, "Intel")))
        {
            var chipset = BestByVersion(entries, e =>
                Contains(e.Title, "Chipset") || Contains(e.Title, "INF") || Contains(e.Category, "Chipset"));
            if (chipset is not null) { return chipset; }
        }

        // Intel LAN (I219 / I225 / I226)
        if (driver.Category == DriverCategory.Network
            && Contains(driver.Provider, "Intel")
            && (Contains(deviceName, "Ethernet") || Contains(deviceName, "LAN")
                || Contains(deviceName, "I219") || Contains(deviceName, "I225") || Contains(deviceName, "I226")))
        {
            var lan = BestByVersion(entries, e =>
                (Contains(e.Title, "LAN") || Contains(e.Title, "Ethernet") || Contains(e.Category, "LAN"))
                && (Contains(e.Title, "Intel") || Contains(e.Category, "Intel")));
            if (lan is not null) { return lan; }
        }

        // Intel Wi-Fi / Bluetooth (AX200, AX201, AX210, ...)
        if (driver.Category is DriverCategory.Network or DriverCategory.Bluetooth
            && Contains(driver.Provider, "Intel")
            && (Contains(deviceName, "Wi-Fi") || Contains(deviceName, "WiFi")
                || Contains(deviceName, "Wireless") || Contains(deviceName, "Bluetooth")
                || Contains(deviceName, "AX20") || Contains(deviceName, "AX21")))
        {
            var wlan = BestByVersion(entries, e =>
                Contains(e.Title, "WiFi") || Contains(e.Title, "Wi-Fi")
                || Contains(e.Title, "Wireless") || Contains(e.Title, "Bluetooth"));
            if (wlan is not null) { return wlan; }
        }

        // MediaTek Bluetooth / Wi-Fi (common on ASUS motherboards)
        if (driver.Category is DriverCategory.Bluetooth or DriverCategory.Network
            && (Contains(driver.Provider, "MediaTek") || Contains(deviceName, "MediaTek")
                || Contains(driver.Manufacturer, "MediaTek")))
        {
            var mt = BestByVersion(entries, e =>
                Contains(e.Title, "Bluetooth") || Contains(e.Title, "WiFi")
                || Contains(e.Title, "Wi-Fi") || Contains(e.Title, "Wireless")
                || Contains(e.Title, "MediaTek") || Contains(e.Category, "Wireless"));
            if (mt is not null) { return mt; }
        }

        // Dirac Audio processing
        if (Contains(driver.Provider, "Dirac") || Contains(deviceName, "Dirac")
            || Contains(driver.Manufacturer, "Dirac"))
        {
            var dirac = BestByVersion(entries, e => Contains(e.Title, "Dirac") || Contains(e.Category, "Dirac"));
            if (dirac is not null) { return dirac; }
        }

        return null;
    }

    // From the entries matching a category, pick the one offering the highest version
    // (date as tie-breaker). Comparing the installed driver against an arbitrary catalog
    // entry could wrongly skip a real update or re-offer an old one; the best entry is
    // the only meaningful thing to compare against.
    private static MotherboardDriverEntry? BestByVersion(
        IReadOnlyList<MotherboardDriverEntry> entries,
        Func<MotherboardDriverEntry, bool> predicate) =>
        entries.Where(predicate)
            .OrderByDescending(e => TryParseEntryVersion(e.Version) ?? new Version(0, 0))
            .ThenByDescending(e => e.ReleaseDate)
            .FirstOrDefault();

    private static Version? TryParseEntryVersion(string raw) =>
        Version.TryParse(raw, out var version) ? version : null;

    internal static bool AreComparableDriverVersions(Version installed, Version catalog)
    {
        if (installed.Major == catalog.Major)
        {
            return true;
        }

        // Some motherboard pages publish a package/display version that is not the
        // WMI driver version. Example from Gigabyte Realtek LAN: package "11.29..."
        // installs a driver reported by Windows as "1125.29...". Numeric comparison
        // would treat every package as older; use date comparison instead.
        if (installed.Major >= 1000 && catalog.Major < 100)
        {
            return false;
        }

        return true;
    }

    private static Version DateToVersion(DateOnly date) => new(date.Year, date.Month, date.Day, 0);

    private static bool Contains(string haystack, string needle) =>
        !string.IsNullOrEmpty(haystack) && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
