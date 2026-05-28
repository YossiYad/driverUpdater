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
        foreach (var driver in drivers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var match = FindMatch(driver, entries);
            if (match is null)
            {
                continue;
            }

            if (driver.CurrentDate is { } currentDate && match.ReleaseDate <= currentDate)
            {
                continue;
            }

            var candidate = BuildCandidate(driver, match, oemInfo.Model);
            _logger.LogInformation(
                "Gigabyte: yielding {InstallKind} candidate for {Device} -> {Url}",
                candidate.InstallKind, driver.DeviceName, candidate.DownloadUrl);
            yield return candidate;
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
        // Map common device-name patterns onto the rough categories that Gigabyte's
        // support pages tend to label downloads with. Each rule fails open and falls
        // through; first match wins.
        var deviceName = driver.DeviceName;

        if (driver.Category is DriverCategory.Audio || Contains(deviceName, "Audio") || Contains(deviceName, "Realtek HD"))
        {
            var audio = entries.FirstOrDefault(e => Contains(e.Category, "Audio") || Contains(e.Title, "Audio") || Contains(e.Title, "Realtek HD"));
            if (audio is not null) { return audio; }
        }

        if (driver.Category == DriverCategory.Network && (Contains(deviceName, "Ethernet") || Contains(deviceName, "GbE") || Contains(deviceName, "LAN")))
        {
            var lan = entries.FirstOrDefault(e => Contains(e.Category, "LAN") || Contains(e.Title, "LAN") || Contains(e.Title, "Ethernet"));
            if (lan is not null) { return lan; }
        }

        if (driver.Category == DriverCategory.Bluetooth || Contains(deviceName, "Bluetooth"))
        {
            var bt = entries.FirstOrDefault(e => Contains(e.Category, "Bluetooth") || Contains(e.Title, "Bluetooth"));
            if (bt is not null) { return bt; }
        }

        if (driver.Category == DriverCategory.Network && (Contains(deviceName, "Wireless") || Contains(deviceName, "Wi-Fi") || Contains(deviceName, "WiFi")))
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
