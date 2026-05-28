using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using DriverUpdater.Services.Sources;
using DriverUpdater.Services.Sources.Internal.Gigabyte;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriverUpdater.Services.Tests.Sources;

public class GigabyteMotherboardSourceTests
{
    [Fact]
    public async Task SearchAsync_yields_vendor_installer_for_matched_audio_driver()
    {
        var entry = new GigabyteDriverEntry(
            Title: "Realtek HD Audio Driver",
            Version: "6.0.9789.1",
            ReleaseDate: new DateOnly(2026, 3, 15),
            DownloadUrl: new Uri("https://download.gigabyte.com/Drivers/audio.zip"),
            SizeBytes: 380_000_000,
            Category: "Audio");

        var source = NewSource(OemVendor.Gigabyte, "B850M GAMING X WIFI6E", new[] { entry });
        var driver = NewDriver("Realtek High Definition Audio", DriverCategory.Audio, "Realtek", new DateOnly(2024, 1, 1));

        var results = await source.SearchAsync(new[] { driver }).ToListAsync();

        results.Should().ContainSingle();
        results[0].InstallKind.Should().Be(UpdateInstallKind.VendorInstaller);
        results[0].SourceUpdateId.Should().StartWith("vendor-installer:installshield:gigabyte:");
        results[0].DownloadUrl.AbsoluteUri.Should().Be("https://download.gigabyte.com/Drivers/audio.zip");
    }

    [Fact]
    public async Task SearchAsync_skips_when_local_driver_already_newer()
    {
        var entry = new GigabyteDriverEntry(
            Title: "Realtek HD Audio Driver",
            Version: "6.0.9789.1",
            ReleaseDate: new DateOnly(2026, 3, 15),
            DownloadUrl: new Uri("https://download.gigabyte.com/Drivers/audio.zip"),
            SizeBytes: 380_000_000,
            Category: "Audio");

        var source = NewSource(OemVendor.Gigabyte, "B850M GAMING X WIFI6E", new[] { entry });
        var driver = NewDriver("Realtek High Definition Audio", DriverCategory.Audio, "Realtek", new DateOnly(2026, 5, 1));

        var results = await source.SearchAsync(new[] { driver }).ToListAsync();

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchAsync_skips_when_oem_is_not_gigabyte()
    {
        var entry = new GigabyteDriverEntry("Anything", "1.0", new DateOnly(2026, 1, 1), new Uri("https://example.com/x.zip"), null, "Audio");
        var source = NewSource(OemVendor.Asus, "Prime X670", new[] { entry });
        var driver = NewDriver("Realtek High Definition Audio", DriverCategory.Audio, "Realtek", new DateOnly(2024, 1, 1));

        var results = await source.SearchAsync(new[] { driver }).ToListAsync();

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchAsync_returns_empty_when_scraper_returns_no_drivers()
    {
        var source = NewSource(OemVendor.Gigabyte, "B850M", Array.Empty<GigabyteDriverEntry>());
        var driver = NewDriver("Realtek High Definition Audio", DriverCategory.Audio, "Realtek", new DateOnly(2024, 1, 1));

        var results = await source.SearchAsync(new[] { driver }).ToListAsync();

        results.Should().BeEmpty();
    }

    [Fact]
    public void FindMatch_matches_audio_device_to_audio_entry()
    {
        var entries = new[]
        {
            new GigabyteDriverEntry("LAN", "1.0", new DateOnly(2026, 1, 1), new Uri("https://example.com/lan.zip"), null, "LAN"),
            new GigabyteDriverEntry("Realtek Audio", "1.0", new DateOnly(2026, 1, 1), new Uri("https://example.com/audio.zip"), null, "Audio")
        };
        var driver = NewDriver("Realtek High Definition Audio", DriverCategory.Audio, "Realtek", new DateOnly(2024, 1, 1));

        var match = GigabyteMotherboardSource.FindMatch(driver, entries);

        match.Should().NotBeNull();
        match!.Category.Should().Be("Audio");
    }

    [Fact]
    public void FindMatch_returns_null_when_no_category_matches()
    {
        var entries = new[]
        {
            new GigabyteDriverEntry("LAN", "1.0", new DateOnly(2026, 1, 1), new Uri("https://example.com/lan.zip"), null, "LAN")
        };
        var driver = NewDriver("Realtek High Definition Audio", DriverCategory.Audio, "Realtek", new DateOnly(2024, 1, 1));

        var match = GigabyteMotherboardSource.FindMatch(driver, entries);

        match.Should().BeNull();
    }

    private static GigabyteMotherboardSource NewSource(OemVendor vendor, string model, IReadOnlyList<GigabyteDriverEntry> entries)
    {
        var oem = new StubOemDetectionService(vendor == OemVendor.Unknown ? null : new OemInfo(vendor, vendor.ToString(), model, "Tool", null, new Uri("https://example.com/")));
        var scraper = new StubScraper(entries);
        return new GigabyteMotherboardSource(oem, scraper, NullLogger<GigabyteMotherboardSource>.Instance);
    }

    private static DriverInfo NewDriver(string deviceName, DriverCategory category, string provider, DateOnly currentDate) => new(
        DeviceId: $"ID\\{deviceName}",
        HardwareId: $"HW\\{deviceName}",
        DeviceName: deviceName,
        Category: category,
        Provider: provider,
        Manufacturer: provider,
        CurrentVersion: new Version(1, 0, 0, 0),
        CurrentDate: currentDate,
        InfName: "oem.inf",
        InfPath: null,
        IsSigned: true,
        DeviceClass: category.ToString());

    private sealed class StubOemDetectionService : IOemDetectionService
    {
        private readonly OemInfo? _info;
        public StubOemDetectionService(OemInfo? info) { _info = info; }
        public Task<OemInfo?> DetectAsync(CancellationToken cancellationToken = default) => Task.FromResult(_info);
    }

    private sealed class StubScraper : IGigabyteScraper
    {
        private readonly IReadOnlyList<GigabyteDriverEntry> _entries;
        public StubScraper(IReadOnlyList<GigabyteDriverEntry> entries) { _entries = entries; }
        public Task<IReadOnlyList<GigabyteDriverEntry>> GetDriversAsync(string motherboardModel, CancellationToken cancellationToken = default)
            => Task.FromResult(_entries);
    }
}
