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
        results[0].SourceUpdateId.Should().StartWith("vendor-installer:nullsoft:gigabyte:");
        results[0].DownloadUrl.AbsoluteUri.Should().Be("https://download.gigabyte.com/Drivers/audio.zip");
    }

    [Fact]
    public async Task SearchAsync_skips_when_local_driver_version_is_newer()
    {
        var entry = NewEntry("Realtek HD Audio Driver", "6.0.9789.1", new DateOnly(2026, 3, 15), "Audio");
        var source = NewSource(OemVendor.Gigabyte, "B850M GAMING X WIFI6E", new[] { entry });
        var driver = NewDriver("Realtek High Definition Audio", DriverCategory.Audio, "Realtek", new DateOnly(2024, 1, 1),
            currentVersion: new Version(7, 0, 0, 0));

        var results = await source.SearchAsync(new[] { driver }).ToListAsync();

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchAsync_skips_when_installed_version_equals_catalog_version()
    {
        // The exact real-world bug: installed Realtek HD Audio 6.0.9927.1, catalog also
        // 6.0.9927.1. Even though Gigabyte's publish date is later than the local INF
        // date, this is the same driver and must not be offered again.
        var entry = NewEntry("Realtek HD Audio Driver", "6.0.9927.1", new DateOnly(2026, 1, 15), "Audio");
        var source = NewSource(OemVendor.Gigabyte, "B850M GAMING X WIFI6E", new[] { entry });
        var driver = NewDriver("Realtek High Definition Audio", DriverCategory.Audio, "Realtek", new DateOnly(2025, 12, 16),
            currentVersion: new Version(6, 0, 9927, 1));

        var results = await source.SearchAsync(new[] { driver }).ToListAsync();

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchAsync_offers_when_catalog_version_is_higher()
    {
        var entry = NewEntry("Realtek HD Audio Driver", "6.0.9999.0", new DateOnly(2026, 1, 15), "Audio");
        var source = NewSource(OemVendor.Gigabyte, "B850M GAMING X WIFI6E", new[] { entry });
        var driver = NewDriver("Realtek High Definition Audio", DriverCategory.Audio, "Realtek", new DateOnly(2025, 12, 16),
            currentVersion: new Version(6, 0, 9927, 1));

        var results = await source.SearchAsync(new[] { driver }).ToListAsync();

        results.Should().ContainSingle();
        results[0].NewVersion.Should().Be(new Version(6, 0, 9999, 0));
    }

    [Fact]
    public async Task SearchAsync_uses_date_when_catalog_package_version_is_not_comparable_to_installed_driver_version()
    {
        var entry = NewEntry("Realtek LAN Driver", "11.29.50.0202", new DateOnly(2026, 6, 23), "LAN", "https://example.com/lan.zip");
        var source = NewSource(OemVendor.Gigabyte, "B850M GAMING X WIFI6E", new[] { entry });
        var driver = NewDriver(
            "Realtek PCIe 2.5GbE Family Controller",
            DriverCategory.Network,
            "Realtek",
            new DateOnly(2025, 12, 24),
            currentVersion: new Version(1125, 28, 20, 1224));

        var results = await source.SearchAsync(new[] { driver }).ToListAsync();

        results.Should().ContainSingle();
        results[0].NewVersion.Should().Be(new Version(2026, 6, 23, 0));
        results[0].IsNewerThan(driver).Should().BeTrue();
    }

    [Fact]
    public void AreComparableDriverVersions_detects_package_versions_that_should_fall_back_to_date()
    {
        MotherboardSource.AreComparableDriverVersions(
                new Version(1125, 28, 20, 1224),
                new Version(11, 29, 50, 202))
            .Should().BeFalse();

        MotherboardSource.AreComparableDriverVersions(
                new Version(6, 0, 9927, 1),
                new Version(6, 0, 9999, 0))
            .Should().BeTrue();
    }

    [Fact]
    public async Task SearchAsync_falls_back_to_date_when_catalog_version_unparseable()
    {
        // No comparable version on the catalog side -> the date gate still applies, so a
        // genuinely older local driver is still offered.
        var entry = NewEntry("Realtek HD Audio Driver", "unknown", new DateOnly(2026, 3, 15), "Audio");
        var source = NewSource(OemVendor.Gigabyte, "B850M GAMING X WIFI6E", new[] { entry });
        var driver = NewDriver("Realtek High Definition Audio", DriverCategory.Audio, "Realtek", new DateOnly(2024, 1, 1),
            currentVersion: new Version(6, 0, 9927, 1));

        var results = await source.SearchAsync(new[] { driver }).ToListAsync();

        results.Should().ContainSingle();
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
        var asusEntry = NewEntry("ASUS Realtek Audio", "2.0", new DateOnly(2026, 5, 1), "Audio", "https://dlcdnets.asus.com/audio.zip");
        var gigabyteEntry = NewEntry("Gigabyte Realtek Audio", "2.0", new DateOnly(2026, 5, 1), "Audio");

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
    public void FindMatch_picks_highest_version_entry_in_category()
    {
        var entries = new[]
        {
            NewEntry("Realtek LAN Driver", "11.21.0903.2024", new DateOnly(2024, 12, 9), "LAN", "https://example.com/old.zip"),
            NewEntry("Realtek LAN Driver", "11.28.20.1224", new DateOnly(2026, 5, 24), "LAN", "https://example.com/new.zip"),
            NewEntry("Realtek LAN Driver", "11.26.50.2025", new DateOnly(2025, 9, 18), "LAN", "https://example.com/mid.zip")
        };
        var driver = NewDriver("Realtek PCIe 2.5GbE Family Controller", DriverCategory.Network, "Realtek", new DateOnly(2024, 1, 1));

        var match = MotherboardSource.FindMatch(driver, entries);

        match.Should().NotBeNull();
        match!.Version.Should().Be("11.28.20.1224");
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

    private static DriverInfo NewDriver(string deviceName, DriverCategory category, string provider, DateOnly currentDate, Version? currentVersion = null) => new(
        DeviceId: $"ID\\{deviceName}",
        HardwareId: $"HW\\{deviceName}",
        DeviceName: deviceName,
        Category: category,
        Provider: provider,
        Manufacturer: provider,
        CurrentVersion: currentVersion ?? new Version(1, 0, 0, 0),
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
