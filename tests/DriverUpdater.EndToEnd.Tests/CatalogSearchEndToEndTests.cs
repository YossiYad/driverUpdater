using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using DriverUpdater.Core.Options;
using DriverUpdater.EndToEnd.Tests.Harness;
using DriverUpdater.Infrastructure.Catalog;
using DriverUpdater.Services.Scanning;
using DriverUpdater.Services.Sources;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriverUpdater.EndToEnd.Tests;

/// <summary>
/// Runs the Microsoft Update Catalog path end to end over real catalog markup: the real
/// <see cref="CatalogHtmlParser"/> turns the search page and the download dialog into hits, the
/// real <see cref="MicrosoftCatalogSource"/> expands hardware-id queries, caches, and maps them
/// into candidates, and the real <see cref="DriverScanService"/> supplies the installed drivers.
/// Only the HTTP transport is stubbed - by serving the same HTML the catalog does.
/// </summary>
public sealed class CatalogSearchEndToEndTests
{
    private const string SearchResultsHtml = """
        <html><body>
        <table id="ctl00_catalogBody_updateMatches">
        <tr><th>Sel</th><th>Title</th><th>Products</th><th>Classification</th><th>Last Updated</th><th>Version</th><th>Size</th><th>Dl</th></tr>
        <tr id="11111111-2222-3333-4444-555555555555_R1">
          <td><input type="checkbox" /></td>
          <td><a>Intel Corporation - Net - 12.19.2.15</a></td>
          <td>Windows 11, Servicing Drivers</td>
          <td>Drivers</td>
          <td>6/1/2024</td>
          <td>12.19.2.15</td>
          <td>2.3 MB</td>
          <td><input type="button" id="11111111-2222-3333-4444-555555555555" value="Download" /></td>
        </tr>
        <tr id="99999999-8888-7777-6666-555555555555_R2">
          <td><input type="checkbox" /></td>
          <td><a>Intel Corporation - Net - 11.0.0.1 (older)</a></td>
          <td>Windows 10, Servicing Drivers</td>
          <td>Drivers</td>
          <td>2/2/2019</td>
          <td>11.0.0.1</td>
          <td>1.1 MB</td>
          <td><input type="button" id="99999999-8888-7777-6666-555555555555" value="Download" /></td>
        </tr>
        </table>
        </body></html>
        """;

    private const string DownloadDialogHtml = """
        <html><body><script>
        var downloadInformation = [];
        downloadInformation[0] = {};
        downloadInformation[0].updateID = '11111111-2222-3333-4444-555555555555';
        downloadInformation[0].files = [];
        downloadInformation[0].files[0] = {};
        downloadInformation[0].files[0].url = 'https://download.windowsupdate.com/c/msdownload/update/driver/drvs/2024/06/intel-net.cab';
        </script></body></html>
        """;

    private static readonly DriverInfo InstalledNic = new(
        DeviceId: @"PCI\VEN_8086&DEV_15F3&SUBSYS_00008086&REV_11\3&11583659&0&FE",
        HardwareId: @"PCI\VEN_8086&DEV_15F3&SUBSYS_00008086&REV_11",
        DeviceName: "Intel(R) Ethernet Connection I219-V",
        Category: DriverCategory.Network,
        Provider: "Intel",
        Manufacturer: "Intel Corporation",
        CurrentVersion: new Version(12, 18, 9, 23),
        CurrentDate: new DateOnly(2021, 3, 15),
        InfName: "oem42.inf",
        InfPath: null,
        IsSigned: true,
        DeviceClass: "NET");

    private static MicrosoftCatalogSource BuildSource(ReplayCatalogHttpClient client) =>
        new(
            client,
            new MemoryCache(new MemoryCacheOptions()),
            new StaticOptionsMonitor<CatalogSettings>(new CatalogSettings
            {
                Enabled = true,
                MaxConcurrentSearches = 4,
                CacheDuration = TimeSpan.FromHours(1)
            }),
            NullLogger<MicrosoftCatalogSource>.Instance);

    [Fact]
    public async Task Real_catalog_markup_becomes_an_installable_candidate_for_the_matching_device()
    {
        var client = new ReplayCatalogHttpClient(SearchResultsHtml, DownloadDialogHtml);
        var source = BuildSource(client);

        var candidates = await source.SearchAsync(new[] { InstalledNic }).ToListAsync();

        var accepted = candidates.Where(c => c.IsNewerThan(InstalledNic)).ToArray();
        accepted.Should().NotBeEmpty();

        var newest = accepted.OrderByDescending(c => c.NewVersion).First();
        newest.SourceUpdateId.Should().Be("11111111-2222-3333-4444-555555555555");
        newest.NewVersion.Should().Be(new Version(12, 19, 2, 15));
        newest.NewDate.Should().Be(new DateOnly(2024, 6, 1));
        newest.InstallKind.Should().Be(UpdateInstallKind.PnPUtilPackage);
        newest.Confidence.Should().Be(UpdateConfidence.Confirmed);
        newest.DownloadUrl.AbsoluteUri.Should().EndWith("intel-net.cab");
        newest.SizeBytes.Should().Be((long)(2.3 * 1024 * 1024));

        accepted.Should().NotContain(
            c => c.SourceUpdateId == "99999999-8888-7777-6666-555555555555",
            "an older catalog hit must never be offered as an update");
    }

    [Fact]
    public async Task A_hit_with_no_downloadable_package_is_offered_as_a_catalog_page_only()
    {
        // The download dialog returns nothing, so there is no direct package to install.
        var client = new ReplayCatalogHttpClient(SearchResultsHtml, "<html><body></body></html>");
        var source = BuildSource(client);

        var candidates = await source.SearchAsync(new[] { InstalledNic }).ToListAsync();

        var newest = candidates.Where(c => c.IsNewerThan(InstalledNic))
            .OrderByDescending(c => c.NewVersion)
            .First();
        newest.InstallKind.Should().Be(UpdateInstallKind.VendorPage);
        newest.Confidence.Should().Be(UpdateConfidence.Advisory);
        newest.DownloadUrl.AbsoluteUri.Should().Contain("catalog.update.microsoft.com/ScopedViewInline.aspx");
    }

    [Fact]
    public async Task The_source_expands_a_full_hardware_id_into_the_broader_queries_the_catalog_indexes()
    {
        var client = new ReplayCatalogHttpClient(SearchResultsHtml, DownloadDialogHtml);
        var source = BuildSource(client);

        await source.SearchAsync(new[] { InstalledNic }).ToListAsync();

        client.SearchedQueries.Should().Contain(@"PCI\VEN_8086&DEV_15F3&SUBSYS_00008086&REV_11");
        client.SearchedQueries.Should().Contain(@"PCI\VEN_8086&DEV_15F3");
        client.SearchedQueries.Should().Contain(@"PCI\VEN_8086&DEV_15F3&SUBSYS_00008086");
    }

    [Fact]
    public async Task Devices_the_catalog_cannot_index_are_never_queried()
    {
        var virtualDevices = new[]
        {
            InstalledNic with
            {
                DeviceId = @"ROOT\SYSTEM\0001",
                HardwareId = @"ROOT\SYSTEM\0001",
                DeviceName = "Microsoft Print to PDF"
            },
            InstalledNic with
            {
                DeviceId = @"SWD\PRINTENUM\{GUID}",
                HardwareId = @"SWD\PRINTENUM\{GUID}",
                DeviceName = "Generic software device"
            }
        };
        var client = new ReplayCatalogHttpClient(SearchResultsHtml, DownloadDialogHtml);
        var source = BuildSource(client);

        var candidates = await source.SearchAsync(virtualDevices).ToListAsync();

        candidates.Should().BeEmpty();
        client.SearchedQueries.Should().BeEmpty("querying the catalog for ROOT/SWD devices is pure noise");
    }

    [Fact]
    public async Task The_same_hardware_id_is_only_fetched_once_thanks_to_the_cache()
    {
        var client = new ReplayCatalogHttpClient(SearchResultsHtml, DownloadDialogHtml);
        var source = BuildSource(client);

        await source.SearchAsync(new[] { InstalledNic }).ToListAsync();
        var firstPass = client.SearchedQueries.Count;
        await source.SearchAsync(new[] { InstalledNic }).ToListAsync();

        firstPass.Should().BeGreaterThan(0);
        client.SearchedQueries.Should().HaveCount(firstPass, "the second scan must be served from the memory cache");
    }

    [Fact]
    public async Task A_catalog_that_is_down_degrades_to_no_candidates_instead_of_throwing()
    {
        var source = BuildSource(new ReplayCatalogHttpClient(SearchResultsHtml, DownloadDialogHtml)
        {
            SearchFailure = new System.Net.Http.HttpRequestException("catalog.update.microsoft.com is unreachable")
        });

        var candidates = await source.SearchAsync(new[] { InstalledNic }).ToListAsync();

        candidates.Should().BeEmpty();
    }

    [Fact]
    public async Task A_disabled_catalog_never_touches_the_network()
    {
        var client = new ReplayCatalogHttpClient(SearchResultsHtml, DownloadDialogHtml);
        var source = new MicrosoftCatalogSource(
            client,
            new MemoryCache(new MemoryCacheOptions()),
            new StaticOptionsMonitor<CatalogSettings>(new CatalogSettings { Enabled = false }),
            NullLogger<MicrosoftCatalogSource>.Instance);

        var candidates = await source.SearchAsync(new[] { InstalledNic }).ToListAsync();

        candidates.Should().BeEmpty();
        client.SearchedQueries.Should().BeEmpty();
    }

    [Fact]
    public async Task Abandoning_a_scan_stops_the_catalog_requests_that_are_still_in_flight()
    {
        // Three devices, so three concurrent catalog searches start. Two of them are made to
        // hang until the test releases them, mimicking slow catalog responses.
        var devices = new[]
        {
            InstalledNic,
            InstalledNic with
            {
                DeviceId = @"PCI\VEN_8086&DEV_9A49\3&11583659&0&02",
                HardwareId = @"PCI\VEN_8086&DEV_9A49",
                DeviceName = "Intel Iris Xe"
            },
            InstalledNic with
            {
                DeviceId = @"PCI\VEN_10EC&DEV_8168\3&11583659&0&03",
                HardwareId = @"PCI\VEN_10EC&DEV_8168",
                DeviceName = "Realtek PCIe GbE"
            }
        };

        var client = new BlockingCatalogHttpClient(SearchResultsHtml, DownloadDialogHtml);
        var source = BuildSource2(client);

        var enumerator = source.SearchAsync(devices).GetAsyncEnumerator();
        try
        {
            (await enumerator.MoveNextAsync()).Should().BeTrue("the first search answers immediately");
            client.InFlight.Should().BeGreaterThan(0, "other searches are still running");
        }
        finally
        {
            // The user cancelled the scan / the consumer walked away.
            await enumerator.DisposeAsync();
        }

        client.InFlight.Should().Be(
            0,
            "abandoning the enumeration must stop the in-flight catalog searches, not orphan them");
    }

    private static MicrosoftCatalogSource BuildSource2(ICatalogHttpClient client) =>
        new(
            client,
            new MemoryCache(new MemoryCacheOptions()),
            new StaticOptionsMonitor<CatalogSettings>(new CatalogSettings
            {
                Enabled = true,
                MaxConcurrentSearches = 8,
                CacheDuration = TimeSpan.FromHours(1)
            }),
            NullLogger<MicrosoftCatalogSource>.Instance);

    /// <summary>Answers the first query at once and holds every later one until cancelled.</summary>
    private sealed class BlockingCatalogHttpClient : ICatalogHttpClient
    {
        private readonly string _searchHtml;
        private readonly string _downloadHtml;
        private int _served;
        private int _inFlight;

        public BlockingCatalogHttpClient(string searchHtml, string downloadHtml)
        {
            _searchHtml = searchHtml;
            _downloadHtml = downloadHtml;
        }

        public int InFlight => Volatile.Read(ref _inFlight);

        public async Task<IReadOnlyList<CatalogSearchHit>> SearchAsync(string query, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _served) > 1)
            {
                Interlocked.Increment(ref _inFlight);
                try
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Decrement(ref _inFlight);
                }
            }

            return CatalogHtmlParser.ParseSearchResults(_searchHtml);
        }

        public Task<IReadOnlyList<CatalogDownloadInfo>> GetDownloadsAsync(
            IReadOnlyCollection<string> updateIds,
            CancellationToken cancellationToken = default)
        {
            var all = CatalogHtmlParser.ParseDownloadDialog(_downloadHtml);
            return Task.FromResult<IReadOnlyList<CatalogDownloadInfo>>(
                all.Where(d => updateIds.Contains(d.UpdateId, StringComparer.OrdinalIgnoreCase)).ToArray());
        }
    }

    /// <summary>Serves catalog HTML through the production parser, exactly as the real client does.</summary>
    private sealed class ReplayCatalogHttpClient : ICatalogHttpClient
    {
        private readonly string _searchHtml;
        private readonly string _downloadHtml;

        public ReplayCatalogHttpClient(string searchHtml, string downloadHtml)
        {
            _searchHtml = searchHtml;
            _downloadHtml = downloadHtml;
        }

        public List<string> SearchedQueries { get; } = new();

        public Exception? SearchFailure { get; init; }

        public Task<IReadOnlyList<CatalogSearchHit>> SearchAsync(string query, CancellationToken cancellationToken = default)
        {
            if (SearchFailure is not null)
            {
                return Task.FromException<IReadOnlyList<CatalogSearchHit>>(SearchFailure);
            }

            lock (SearchedQueries)
            {
                SearchedQueries.Add(query);
            }
            return Task.FromResult(CatalogHtmlParser.ParseSearchResults(_searchHtml));
        }

        public Task<IReadOnlyList<CatalogDownloadInfo>> GetDownloadsAsync(
            IReadOnlyCollection<string> updateIds,
            CancellationToken cancellationToken = default)
        {
            var all = CatalogHtmlParser.ParseDownloadDialog(_downloadHtml);
            return Task.FromResult<IReadOnlyList<CatalogDownloadInfo>>(
                all.Where(d => updateIds.Contains(d.UpdateId, StringComparer.OrdinalIgnoreCase)).ToArray());
        }
    }
}
