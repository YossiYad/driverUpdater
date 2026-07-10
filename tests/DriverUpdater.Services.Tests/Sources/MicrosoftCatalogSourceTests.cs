using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using DriverUpdater.Core.Options;
using DriverUpdater.Services.Sources;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DriverUpdater.Services.Tests.Sources;

public class MicrosoftCatalogSourceTests
{
    [Fact]
    public async Task SearchAsync_yields_nothing_when_disabled()
    {
        var fakeClient = new FakeCatalogClient();
        var source = NewSource(fakeClient, enabled: false);

        var drivers = new[] { NewDriver("PCI\\VEN_1") };
        var results = await source.SearchAsync(drivers).ToListAsync();

        results.Should().BeEmpty();
        fakeClient.SearchInvocations.Should().Be(0);
    }

    [Fact]
    public async Task SearchAsync_queries_catalog_for_each_unique_hardware_id()
    {
        var fakeClient = new FakeCatalogClient
        {
            HitsByQuery =
            {
                ["PCI\\VEN_1"] = new[] { Hit("guid-1", "Driver A", new Version(2, 0, 0, 0), new DateOnly(2026, 1, 1)) },
                ["PCI\\VEN_2"] = new[] { Hit("guid-2", "Driver B", new Version(3, 0, 0, 0), new DateOnly(2026, 2, 2)) }
            },
            DownloadsById =
            {
                ["guid-1"] = new CatalogDownloadInfo("guid-1", new Uri("https://download.example.com/a.cab"), 1024),
                ["guid-2"] = new CatalogDownloadInfo("guid-2", new Uri("https://download.example.com/b.cab"), 2048)
            }
        };

        var source = NewSource(fakeClient, enabled: true);

        var drivers = new[]
        {
            NewDriver("PCI\\VEN_1"),
            NewDriver("PCI\\VEN_1"),
            NewDriver("PCI\\VEN_2"),
            NewDriver("")
        };

        var results = await source.SearchAsync(drivers).ToListAsync();

        results.Should().HaveCount(2);
        results.Should().Contain(c => c.ForHardwareId == "PCI\\VEN_1" && c.NewVersion == new Version(2, 0, 0, 0));
        results.Should().Contain(c => c.ForHardwareId == "PCI\\VEN_2" && c.NewVersion == new Version(3, 0, 0, 0));
        fakeClient.SearchInvocations.Should().Be(2);
    }

    [Fact]
    public async Task SearchAsync_queries_catalog_for_alternate_hardware_ids()
    {
        var fakeClient = new FakeCatalogClient
        {
            HitsByQuery =
            {
                ["PCI\\VEN_8086&DEV_1234&SUBSYS_00000000"] =
                    new[] { Hit("guid-alt", "Driver Alt", new Version(2, 0, 0, 0), new DateOnly(2026, 1, 1)) }
            },
            DownloadsById =
            {
                ["guid-alt"] = new CatalogDownloadInfo("guid-alt", new Uri("https://download.example.com/alt.cab"), 1024)
            }
        };

        var source = NewSource(fakeClient, enabled: true);
        var driver = NewDriver("PCI\\VEN_8086&DEV_1234&REV_01") with
        {
            HardwareIds =
            [
                "PCI\\VEN_8086&DEV_1234&REV_01",
                "PCI\\VEN_8086&DEV_1234&SUBSYS_00000000"
            ]
        };

        var results = await source.SearchAsync(new[] { driver }).ToListAsync();

        results.Should().ContainSingle(c => c.ForHardwareId == "PCI\\VEN_8086&DEV_1234&SUBSYS_00000000");
        fakeClient.SearchInvocations.Should().BeGreaterThan(1);
    }

    [Fact]
    public async Task SearchAsync_caches_hits_per_hardware_id()
    {
        var fakeClient = new FakeCatalogClient
        {
            HitsByQuery =
            {
                ["PCI\\VEN_1"] = new[] { Hit("guid-1", "Driver A", new Version(2, 0, 0, 0), new DateOnly(2026, 1, 1)) }
            }
        };

        var source = NewSource(fakeClient, enabled: true);
        var drivers = new[] { NewDriver("PCI\\VEN_1") };

        await source.SearchAsync(drivers).ToListAsync();
        await source.SearchAsync(drivers).ToListAsync();

        fakeClient.SearchInvocations.Should().Be(1);
    }

    [Fact]
    public async Task SearchAsync_continues_when_a_single_search_throws()
    {
        var fakeClient = new FakeCatalogClient
        {
            HitsByQuery =
            {
                ["PCI\\VEN_OK"] = new[] { Hit("guid-ok", "Good", new Version(1, 0, 0, 0), new DateOnly(2026, 1, 1)) }
            },
            FailQueries = { "PCI\\VEN_BAD" }
        };

        var source = NewSource(fakeClient, enabled: true);
        var drivers = new[] { NewDriver("PCI\\VEN_BAD"), NewDriver("PCI\\VEN_OK") };

        var results = await source.SearchAsync(drivers).ToListAsync();

        results.Should().ContainSingle();
        results[0].ForHardwareId.Should().Be("PCI\\VEN_OK");
    }

    [Fact]
    public void TryMap_uses_download_info_when_present()
    {
        var hit = Hit("guid-x", "Sample", new Version(5, 0, 0, 0), new DateOnly(2026, 1, 1), sizeBytes: 100);
        var downloads = new Dictionary<string, CatalogDownloadInfo>
        {
            ["guid-x"] = new CatalogDownloadInfo("guid-x", new Uri("https://download.example.com/x.cab"), 999)
        };

        var ok = MicrosoftCatalogSource.TryMap(hit, "PCI\\HW_1", downloads, out var candidate);

        ok.Should().BeTrue();
        candidate.DownloadUrl.Should().Be(new Uri("https://download.example.com/x.cab"));
        candidate.SizeBytes.Should().Be(999);
        candidate.Source.Should().Be(UpdateSource.MicrosoftCatalog);
        candidate.SourceUpdateId.Should().Be("guid-x");
    }

    [Fact]
    public void TryMap_discards_hit_when_download_is_a_non_driver_executable()
    {
        var hit = Hit("guid-exe", "Windows Root Certificates", new Version(2021, 12, 5, 0), new DateOnly(2021, 12, 5), sizeBytes: 100);
        var downloads = new Dictionary<string, CatalogDownloadInfo>
        {
            ["guid-exe"] = new CatalogDownloadInfo("guid-exe", new Uri("https://download.example.com/rootsupd_fe44934f.exe"), 999)
        };

        var ok = MicrosoftCatalogSource.TryMap(hit, "ROOT\\HYPERV", downloads, out var candidate);

        ok.Should().BeFalse();
        candidate.Should().BeNull();
    }

    [Fact]
    public void TryMap_falls_back_to_scoped_view_url_when_no_download_info()
    {
        var hit = Hit("guid-z", "Sample", new Version(5, 0, 0, 0), new DateOnly(2026, 1, 1), sizeBytes: 100);

        var ok = MicrosoftCatalogSource.TryMap(hit, "PCI\\HW_1", new Dictionary<string, CatalogDownloadInfo>(), out var candidate);

        ok.Should().BeTrue();
        candidate.DownloadUrl.AbsoluteUri.Should().StartWith("https://www.catalog.update.microsoft.com/ScopedViewInline.aspx");
        candidate.DownloadUrl.AbsoluteUri.Should().Contain("guid-z");
    }

    [Fact]
    public void ExpandHardwareIdQueries_adds_less_specific_pci_and_usb_queries()
    {
        MicrosoftCatalogSource.ExpandHardwareIdQueries(@"PCI\VEN_1002&DEV_747E&SUBSYS_24141458&REV_FF")
            .Should().BeEquivalentTo([
                @"PCI\VEN_1002&DEV_747E&SUBSYS_24141458&REV_FF",
                @"PCI\VEN_1002&DEV_747E",
                @"PCI\VEN_1002&DEV_747E&SUBSYS_24141458"
            ], options => options.WithStrictOrdering());

        MicrosoftCatalogSource.ExpandHardwareIdQueries(@"USB\VID_046D&PID_C548&MI_00")
            .Should().BeEquivalentTo([
                @"USB\VID_046D&PID_C548&MI_00",
                @"USB\VID_046D&PID_C548"
            ], options => options.WithStrictOrdering());
    }

    private static MicrosoftCatalogSource NewSource(ICatalogHttpClient client, bool enabled)
    {
        var settings = new CatalogSettings { Enabled = enabled, MaxConcurrentSearches = 4, CacheDuration = TimeSpan.FromHours(1) };
        var monitor = new ConstantOptionsMonitor<CatalogSettings>(settings);
        var cache = new MemoryCache(new MemoryCacheOptions());
        return new MicrosoftCatalogSource(client, cache, monitor, NullLogger<MicrosoftCatalogSource>.Instance);
    }

    private static DriverInfo NewDriver(string hardwareId) => new(
        DeviceId: $"DEV\\{hardwareId}",
        HardwareId: hardwareId,
        DeviceName: $"Device {hardwareId}",
        Category: DriverCategory.Other,
        Provider: "Test",
        Manufacturer: "Test",
        CurrentVersion: new Version(1, 0, 0, 0),
        CurrentDate: new DateOnly(2024, 1, 1),
        InfName: null,
        InfPath: null,
        IsSigned: true,
        DeviceClass: "Test");

    private static CatalogSearchHit Hit(string id, string title, Version version, DateOnly date, long? sizeBytes = 1024) =>
        new(UpdateId: id, Title: title, Products: null, Classification: "Drivers",
            LastUpdatedText: date.ToString("yyyy-MM-dd"), LastUpdatedDate: date,
            VersionText: version.ToString(), Version: version,
            SizeText: $"{sizeBytes} B", SizeBytes: sizeBytes);

    private sealed class FakeCatalogClient : ICatalogHttpClient
    {
        public Dictionary<string, IReadOnlyList<CatalogSearchHit>> HitsByQuery { get; } = new();
        public Dictionary<string, CatalogDownloadInfo> DownloadsById { get; } = new();
        public HashSet<string> FailQueries { get; } = new();
        public int SearchInvocations { get; private set; }

        public Task<IReadOnlyList<CatalogSearchHit>> SearchAsync(string query, CancellationToken cancellationToken = default)
        {
            SearchInvocations++;
            if (FailQueries.Contains(query))
            {
                throw new InvalidOperationException("simulated failure");
            }
            return Task.FromResult(HitsByQuery.TryGetValue(query, out var hits) ? hits : Array.Empty<CatalogSearchHit>());
        }

        public Task<IReadOnlyList<CatalogDownloadInfo>> GetDownloadsAsync(
            IReadOnlyCollection<string> updateIds,
            CancellationToken cancellationToken = default)
        {
            var matches = updateIds
                .Where(DownloadsById.ContainsKey)
                .Select(id => DownloadsById[id])
                .ToArray();
            return Task.FromResult<IReadOnlyList<CatalogDownloadInfo>>(matches);
        }
    }

    private sealed class ConstantOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public ConstantOptionsMonitor(T value) { CurrentValue = value; }
        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string> listener) => null;
    }
}

public static class AsyncEnumerableTestExtensions
{
    public static async Task<List<T>> ToListAsync<T>(this IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source)
        {
            list.Add(item);
        }
        return list;
    }
}
