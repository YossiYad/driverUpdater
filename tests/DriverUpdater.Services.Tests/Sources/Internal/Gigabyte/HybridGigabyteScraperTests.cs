using DriverUpdater.Core.Options;
using DriverUpdater.Services.Sources.Internal.Gigabyte;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DriverUpdater.Services.Tests.Sources.Internal.Gigabyte;

public class HybridGigabyteScraperTests
{
    [Fact]
    public async Task GetDriversAsync_returns_api_result_when_api_succeeds()
    {
        var apiEntries = new[] { NewEntry("Audio") };
        var api = new FakeScraper(apiEntries);
        var playwright = new FakeScraper(new[] { NewEntry("LAN") });
        var hybrid = NewHybrid(api, playwright, playwrightEnabled: true);

        var result = await hybrid.GetDriversAsync("B850M");

        result.Should().BeEquivalentTo(apiEntries);
        playwright.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task GetDriversAsync_falls_back_to_playwright_when_api_throws_and_setting_is_on()
    {
        var api = new FakeScraper(throwOnCall: true);
        var playwrightEntries = new[] { NewEntry("LAN") };
        var playwright = new FakeScraper(playwrightEntries);
        var hybrid = NewHybrid(api, playwright, playwrightEnabled: true);

        var result = await hybrid.GetDriversAsync("B850M");

        result.Should().BeEquivalentTo(playwrightEntries);
        playwright.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task GetDriversAsync_returns_empty_when_api_throws_and_playwright_is_disabled()
    {
        var api = new FakeScraper(throwOnCall: true);
        var playwright = new FakeScraper(new[] { NewEntry("Audio") });
        var hybrid = NewHybrid(api, playwright, playwrightEnabled: false);

        var result = await hybrid.GetDriversAsync("B850M");

        result.Should().BeEmpty();
        playwright.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task GetDriversAsync_returns_empty_when_playwright_also_throws()
    {
        var api = new FakeScraper(throwOnCall: true);
        var playwright = new FakeScraper(throwOnCall: true);
        var hybrid = NewHybrid(api, playwright, playwrightEnabled: true);

        var result = await hybrid.GetDriversAsync("B850M");

        result.Should().BeEmpty();
    }

    private static GigabyteDriverEntry NewEntry(string category) => new(
        Title: $"{category} Driver",
        Version: "1.0",
        ReleaseDate: new DateOnly(2026, 1, 1),
        DownloadUrl: new Uri("https://download.gigabyte.com/test.zip"),
        SizeBytes: 10_000_000,
        Category: category);

    private static HybridGigabyteScraper NewHybrid(IGigabyteScraper api, IGigabyteScraper playwright, bool playwrightEnabled)
    {
        var settings = new TestOptionsMonitor(new ScraperSettings { EnablePlaywrightFallback = playwrightEnabled });
        return new HybridGigabyteScraper(
            api,
            new Lazy<IGigabyteScraper>(() => playwright),
            settings,
            NullLogger<HybridGigabyteScraper>.Instance);
    }

    private sealed class FakeScraper : IGigabyteScraper
    {
        private readonly IReadOnlyList<GigabyteDriverEntry> _entries;
        private readonly bool _throwOnCall;

        public FakeScraper(IReadOnlyList<GigabyteDriverEntry>? entries = null, bool throwOnCall = false)
        {
            _entries = entries ?? Array.Empty<GigabyteDriverEntry>();
            _throwOnCall = throwOnCall;
        }

        public int CallCount { get; private set; }

        public Task<IReadOnlyList<GigabyteDriverEntry>> GetDriversAsync(string motherboardModel, CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (_throwOnCall)
            {
                throw new ScraperUnavailableException("forced");
            }
            return Task.FromResult(_entries);
        }
    }

    private sealed class TestOptionsMonitor : IOptionsMonitor<ScraperSettings>
    {
        public TestOptionsMonitor(ScraperSettings settings) { CurrentValue = settings; }
        public ScraperSettings CurrentValue { get; }
        public ScraperSettings Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<ScraperSettings, string?> listener) => null;
    }
}
