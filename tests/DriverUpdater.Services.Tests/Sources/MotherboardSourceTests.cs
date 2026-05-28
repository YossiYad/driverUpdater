using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using DriverUpdater.Services.Sources;
using DriverUpdater.Services.Sources.Internal.Motherboard;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriverUpdater.Services.Tests.Sources;

public class MotherboardSourceTests
{
    [Fact]
    public async Task SearchAsync_yields_vendor_installer_for_matched_audio_driver()
    {
        var entry = NewEntry("Realtek HD Audio Driver", "6.0.9789.1", new DateOnly(2026, 3, 15), "Audio");
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
        var entry = NewEntry("Realtek HD Audio Driver", "6.0.9789.1", new DateOnly(2026, 3, 15), "Audio");
        var source = NewSource(OemVendor.Gigabyte, "B850M GAMING X WIFI6E", new[] { entry });
        var driver = NewDriver("Realtek High Definition Audio", DriverCategory.Audio, "Realtek", new DateOnly(2026, 5, 1));

        var results = await source.SearchAsync(new[] { driver }).ToListAsync();

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchAsync_skips_when_vendor_has_no_scraper_registered()
    {
        // Acer is supported by OemVendor enum but no scraper is wired up; the dispatcher
        // should log and short-circuit.
        var source = new MotherboardSource(
            new StubOemDetectionService(new OemInfo(OemVendor.Acer, "Acer", "Aspire 5", "Acer Care Center", null, new Uri("https://acer.com"))),
            new Dictionary<OemVendor, IMotherboardScraper>
            {
                [OemVendor.Gigabyte] = new StubScraper(Array.Empty<MotherboardDriverEntry>())
            },
            NullLogger<MotherboardSource>.Instance);
        var driver = NewDriver("Realtek High Definition Audio", DriverCategory.Audio, "Realtek", new DateOnly(2024, 1, 1));

        var results = await source.SearchAsync(new[] { driver }).ToListAsync();

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchAsync_dispatches_to_the_correct_vendor_scraper()
    {
        // Register both Gigabyte and Asus scrapers but the OEM detection reports Asus.
        // The Asus stub returns a different entry, and we expect that entry to win.
        var asusEntry = NewEntry("ASUS Realtek Audio", "1.0", new DateOnly(2026, 5, 1), "Audio", "https://dlcdnets.asus.com/audio.zip");
        var gigabyteEntry = NewEntry("Gigabyte Realtek Audio", "1.0", new DateOnly(2026, 5, 1), "Audio");

        var source = new MotherboardSource(
            new StubOemDetectionService(new OemInfo(OemVendor.Asus, "Asus", "Prime X670", "MyASUS", null, new Uri("https://asus.com"))),
            new Dictionary<OemVendor, IMotherboardScraper>
            {
                [OemVendor.Gigabyte] = new StubScraper(new[] { gigabyteEntry }),
                [OemVendor.Asus] = new StubScraper(new[] { asusEntry })
            },
            NullLogger<MotherboardSource>.Instance);
        var driver = NewDriver("Realtek High Definition Audio", DriverCategory.Audio, "Realtek", new DateOnly(2024, 1, 1));

        var results = await source.SearchAsync(new[] { driver }).ToListAsync();

        results.Should().ContainSingle();
        results[0].DownloadUrl.AbsoluteUri.Should().Be("https://dlcdnets.asus.com/audio.zip");
        results[0].SourceUpdateId.Should().StartWith("vendor-installer:installshield:asus:");
    }

    [Fact]
    public async Task SearchAsync_returns_empty_when_scraper_returns_no_drivers()
    {
        var source = NewSource(OemVendor.Gigabyte, "B850M", Array.Empty<MotherboardDriverEntry>());
        var driver = NewDriver("Realtek High Definition Audio", DriverCategory.Audio, "Realtek", new DateOnly(2024, 1, 1));

        var results = await source.SearchAsync(new[] { driver }).ToListAsync();

        results.Should().BeEmpty();
    }

    [Fact]
    public void FindMatch_matches_audio_device_to_audio_entry()
    {
        var entries = new[]
        {
            NewEntry("LAN", "1.0", new DateOnly(2026, 1, 1), "LAN", "https://example.com/lan.zip"),
            NewEntry("Realtek Audio", "1.0", new DateOnly(2026, 1, 1), "Audio", "https://example.com/audio.zip")
        };
        var driver = NewDriver("Realtek High Definition Audio", DriverCategory.Audio, "Realtek", new DateOnly(2024, 1, 1));

        var match = MotherboardSource.FindMatch(driver, entries);

        match.Should().NotBeNull();
        match!.Category.Should().Be("Audio");
    }

    [Fact]
    public void FindMatch_returns_null_when_no_category_matches()
    {
        var entries = new[]
        {
            NewEntry("LAN", "1.0", new DateOnly(2026, 1, 1), "LAN", "https://example.com/lan.zip")
        };
        var driver = NewDriver("Realtek High Definition Audio", DriverCategory.Audio, "Realtek", new DateOnly(2024, 1, 1));

        var match = MotherboardSource.FindMatch(driver, entries);

        match.Should().BeNull();
    }

    private static MotherboardDriverEntry NewEntry(string title, string version, DateOnly date, string category, string url = "https://download.gigabyte.com/Drivers/audio.zip") =>
        new(title, version, date, new Uri(url), SizeBytes: 380_000_000, category);

    private static MotherboardSource NewSource(OemVendor vendor, string model, IReadOnlyList<MotherboardDriverEntry> entries)
    {
        var oem = new StubOemDetectionService(vendor == OemVendor.Unknown ? null : new OemInfo(vendor, vendor.ToString(), model, "Tool", null, new Uri("https://example.com/")));
        var scrapers = new Dictionary<OemVendor, IMotherboardScraper>
        {
            [vendor] = new StubScraper(entries)
        };
        return new MotherboardSource(oem, scrapers, NullLogger<MotherboardSource>.Instance);
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

    private sealed class StubScraper : IMotherboardScraper
    {
        private readonly IReadOnlyList<MotherboardDriverEntry> _entries;
        public StubScraper(IReadOnlyList<MotherboardDriverEntry> entries) { _entries = entries; }
        public Task<IReadOnlyList<MotherboardDriverEntry>> GetDriversAsync(string motherboardModel, CancellationToken cancellationToken = default)
            => Task.FromResult(_entries);
    }
}
