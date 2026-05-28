using System.Runtime.CompilerServices;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using DriverUpdater.Services.Sources.Internal.Gigabyte;
using Microsoft.Extensions.Logging;

namespace DriverUpdater.Services.Sources;

public sealed class GigabyteMotherboardSource : IUpdateSource
{
    private readonly IOemDetectionService _oem;
    private readonly IGigabyteScraper _scraper;
    private readonly ILogger<GigabyteMotherboardSource> _logger;

    public GigabyteMotherboardSource(
        IOemDetectionService oem,
        IGigabyteScraper scraper,
        ILogger<GigabyteMotherboardSource> logger)
    {
        ArgumentNullException.ThrowIfNull(oem);
        ArgumentNullException.ThrowIfNull(scraper);
        ArgumentNullException.ThrowIfNull(logger);
        _oem = oem;
        _scraper = scraper;
        _logger = logger;
    }

    public UpdateSource Kind => UpdateSource.Oem;

    public string DisplayName => "Gigabyte motherboard drivers";

    public async IAsyncEnumerable<UpdateCandidate> SearchAsync(
        IReadOnlyCollection<DriverInfo> drivers,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(drivers);

        var oemInfo = await _oem.DetectAsync(cancellationToken).ConfigureAwait(false);
        if (oemInfo is null || oemInfo.Vendor != OemVendor.Gigabyte)
        {
            _logger.LogInformation("Gigabyte source skipped (vendor={Vendor})", oemInfo?.Vendor.ToString() ?? "Unknown");
            yield break;
        }

        if (string.IsNullOrWhiteSpace(oemInfo.Model))
        {
            _logger.LogInformation("Gigabyte source skipped: empty motherboard model");
            yield break;
        }

        IReadOnlyList<GigabyteDriverEntry> entries;
        try
        {
            entries = await _scraper.GetDriversAsync(oemInfo.Model, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Gigabyte scraper failed for {Model}", oemInfo.Model);
            yield break;
        }

        if (entries.Count == 0)
        {
            _logger.LogInformation("Gigabyte scraper returned 0 entries for {Model}", oemInfo.Model);
            yield break;
        }

        _logger.LogInformation("Gigabyte: matching {Count} catalog entries against {DriverCount} installed drivers", entries.Count, drivers.Count);
        foreach (var entry in entries)
        {
            _logger.LogInformation(
                "Gigabyte catalog entry: title={Title} version={Version} date={Date} category={Category} url={Url}",
                entry.Title, entry.Version, entry.ReleaseDate, entry.Category, entry.DownloadUrl);
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
            if (driver.CurrentDate is { } currentDate && match.ReleaseDate <= currentDate)
            {
                _logger.LogInformation(
                    "Gigabyte: skipping {Device} - local driver date {LocalDate} is at or newer than catalog {RemoteDate}",
                    driver.DeviceName, currentDate, match.ReleaseDate);
                continue;
            }

            var candidate = BuildCandidate(driver, match, oemInfo.Model);
            _logger.LogInformation(
                "Gigabyte: yielding {InstallKind} candidate for {Device} -> {Url}",
                candidate.InstallKind, driver.DeviceName, candidate.DownloadUrl);
            yield return candidate;
        }

        if (matched == 0)
        {
            _logger.LogInformation("Gigabyte: 0 of {Count} installed drivers matched any catalog entry by category heuristic", drivers.Count);
        }
    }

    internal static UpdateCandidate BuildCandidate(DriverInfo driver, GigabyteDriverEntry entry, string model) =>
        new(
            ForHardwareId: driver.HardwareId,
            Source: UpdateSource.Oem,
            NewVersion: DateToVersion(entry.ReleaseDate),
            NewDate: entry.ReleaseDate,
            DownloadUrl: entry.DownloadUrl,
            SizeBytes: entry.SizeBytes ?? 0,
            KbArticle: null,
            IsSuperseded: false,
            SourceUpdateId: $"vendor-installer:installshield:gigabyte:{model}:{entry.Title}:{entry.Version}",
            SupersededIds: Array.Empty<string>(),
            InstallKind: UpdateInstallKind.VendorInstaller);

    internal static GigabyteDriverEntry? FindMatch(DriverInfo driver, IReadOnlyList<GigabyteDriverEntry> entries)
    {
        // Skip Microsoft-provided drivers entirely. Windows ships virtual devices like
        // "Microsoft Bluetooth LE Enumerator", "Bluetooth HID Device", "Audio Endpoint",
        // and "Microsoft Streaming Service Proxy" that Windows Update keeps current -
        // pushing a Realtek/Gigabyte installer at them produces noise without value.
        if (Contains(driver.Provider, "Microsoft") || Contains(driver.Manufacturer, "Microsoft"))
        {
            return null;
        }

        // Skip generic Bluetooth peripherals (mouse/keyboard/headset). These show up
        // with the Bluetooth category but their provider is Microsoft (filtered above)
        // or a peripheral vendor (Logitech/Razer/etc.); the Realtek BT host driver does
        // not apply to them.
        if (Contains(driver.DeviceName, "HID") || Contains(driver.DeviceName, "Personal Area Network")
            || Contains(driver.DeviceName, "Enumerator") || Contains(driver.DeviceName, "Identification Service"))
        {
            return null;
        }

        var deviceName = driver.DeviceName;

        // Audio: only Realtek-branded audio chips on this board. AMD's HD Audio Device,
        // AMD Streaming Audio, and AMD-Dynamic Audio are AMD GPU audio components handled
        // by the Adrenalin installer - skip them here too so we do not double-install.
        if (driver.Category == DriverCategory.Audio
            && (Contains(driver.Provider, "Realtek") || Contains(deviceName, "Realtek")))
        {
            var audio = entries.FirstOrDefault(e =>
                (Contains(e.Category, "Audio") || Contains(e.Title, "Audio"))
                && Contains(e.Title, "Realtek")
                && !Contains(e.Title, "LE Audio"));
            if (audio is not null) { return audio; }
        }

        if (driver.Category == DriverCategory.Network
            && (Contains(driver.Provider, "Realtek") || Contains(deviceName, "Realtek"))
            && (Contains(deviceName, "Ethernet") || Contains(deviceName, "GbE") || Contains(deviceName, "LAN")))
        {
            var lan = entries.Where(e => Contains(e.Category, "LAN") || Contains(e.Title, "LAN"))
                .OrderByDescending(e => e.ReleaseDate)
                .FirstOrDefault();
            if (lan is not null) { return lan; }
        }

        if (driver.Category == DriverCategory.Bluetooth
            && (Contains(driver.Provider, "Realtek") || Contains(deviceName, "Realtek")))
        {
            var bt = entries.FirstOrDefault(e => Contains(e.Category, "Bluetooth") || Contains(e.Title, "Bluetooth"));
            if (bt is not null) { return bt; }
        }

        if (driver.Category == DriverCategory.Network
            && (Contains(driver.Provider, "Realtek") || Contains(deviceName, "Realtek"))
            && (Contains(deviceName, "Wireless") || Contains(deviceName, "Wi-Fi") || Contains(deviceName, "WiFi")))
        {
            var wifi = entries.FirstOrDefault(e => Contains(e.Category, "Wireless") || Contains(e.Title, "WiFi") || Contains(e.Title, "Wireless"));
            if (wifi is not null) { return wifi; }
        }

        return null;
    }

    private static Version DateToVersion(DateOnly date) => new(date.Year, date.Month, date.Day, 0);

    private static bool Contains(string haystack, string needle) =>
        !string.IsNullOrEmpty(haystack) && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
