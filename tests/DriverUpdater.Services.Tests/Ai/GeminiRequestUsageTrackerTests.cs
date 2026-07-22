using DriverUpdater.Services.Ai;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriverUpdater.Services.Tests.Ai;

public sealed class GeminiRequestUsageTrackerTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "DriverUpdater.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void RecordRequest_counts_by_model_and_persists_between_instances()
    {
        var path = Path.Combine(_directory, "usage.json");
        var clock = new StubTimeProvider(new DateTimeOffset(2026, 7, 22, 12, 0, 0, TimeSpan.Zero));
        var tracker = NewTracker(path, clock);

        tracker.RecordRequest("gemini-2.5-flash");
        tracker.RecordRequest("gemini-2.5-flash");
        tracker.RecordRequest("gemini-3.5-flash");

        NewTracker(path, clock).GetRequestsToday("gemini-2.5-flash").Should().Be(2);
        NewTracker(path, clock).GetRequestsToday("gemini-3.5-flash").Should().Be(1);
    }

    [Fact]
    public void GetRequestsToday_resets_at_the_next_pacific_day()
    {
        var path = Path.Combine(_directory, "usage.json");
        var clock = new StubTimeProvider(new DateTimeOffset(2026, 7, 22, 12, 0, 0, TimeSpan.Zero));
        var tracker = NewTracker(path, clock);
        tracker.RecordRequest("gemini-2.5-flash");

        clock.UtcNow = new DateTimeOffset(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);

        tracker.GetRequestsToday("gemini-2.5-flash").Should().Be(0);
    }

    [Fact]
    public async Task Usage_handler_counts_each_outgoing_gemini_request_by_model()
    {
        var path = Path.Combine(_directory, "usage.json");
        var clock = new StubTimeProvider(new DateTimeOffset(2026, 7, 22, 12, 0, 0, TimeSpan.Zero));
        var tracker = NewTracker(path, clock);
        using var handler = new GeminiUsageTrackingHandler(tracker)
        {
            InnerHandler = new OkHandler()
        };
        using var client = new HttpClient(handler);

        await client.GetAsync(
            "https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent");
        await client.GetAsync(
            "https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent");
        await client.GetAsync("https://example.com/not-gemini");

        tracker.GetRequestsToday("gemini-2.5-flash").Should().Be(2);
    }

    private static GeminiRequestUsageTracker NewTracker(string path, TimeProvider clock) =>
        new(NullLogger<GeminiRequestUsageTracker>.Instance, path, clock);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class StubTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class OkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
    }
}
