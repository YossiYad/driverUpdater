using System.Net;
using System.Net.Http;
using DriverUpdater.Core.Models;
using DriverUpdater.Core.Options;
using DriverUpdater.EndToEnd.Tests.Harness;
using DriverUpdater.Services.Ai;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriverUpdater.EndToEnd.Tests;

/// <summary>
/// Runs the real <see cref="GeminiAiVerifier"/> and the real <see cref="GeminiQuotaGate"/> over a
/// stubbed transport that answers with genuine Gemini response shapes: prompt building, HTTP
/// headers, JSON extraction, verdict parsing, API-key rotation on 429, and quota blocking all run
/// as they do in production.
/// </summary>
public sealed class AiVerificationEndToEndTests
{
    private static AiVerificationRequest Request(string id = "catalog-1") => new(
        CorrelationId: id,
        DeviceName: "NVIDIA GeForce RTX 3070",
        HardwareId: @"PCI\VEN_10DE&DEV_2484",
        InstalledVersion: "31.0.15.3623",
        InstalledDate: new DateOnly(2023, 5, 1),
        CandidateVersion: "32.0.15.6094",
        CandidateDate: new DateOnly(2024, 7, 15),
        Source: UpdateSource.MicrosoftCatalog,
        DownloadUrl: "https://catalog.update.microsoft.com/download/nvidia.cab",
        Category: DriverCategory.Display,
        Provider: "NVIDIA",
        Manufacturer: "NVIDIA",
        InstallKind: UpdateInstallKind.PnPUtilPackage,
        Confidence: UpdateConfidence.Confirmed);

    private static string GeminiEnvelope(string modelText)
    {
        var escaped = System.Text.Json.JsonSerializer.Serialize(modelText);
        return $$"""
            {"candidates":[{"content":{"parts":[{"text":{{escaped}}}],"role":"model"},"finishReason":"STOP"}]}
            """;
    }

    private static string VerdictJson(string id, bool genuinelyNewer, string risk) => $$"""
        Here is my analysis of the driver you asked about.

        ```json
        {"verdicts":[{"id":"{{id}}","isGenuinelyNewer":{{(genuinelyNewer ? "true" : "false")}},"risk":"{{risk}}",
        "summary":"Recommended","rationale":"The candidate is a newer branch with no reported regressions.",
        "latestKnownVersion":"32.0.15.6094","latestKnownDate":"2024-07-15",
        "latestKnownUrl":"https://www.nvidia.com/download/driverResults.aspx/1234",
        "installedSuitability":"The installed driver is stable but a year old.",
        "candidateSuitability":"Matches this exact GPU and Windows build.",
        "recommendedVersion":"32.0.15.6094","advisorNote":"Safe to install."}]}
        ```
        """;

    private static GeminiAiVerifier BuildVerifier(
        ScriptedGeminiHandler handler,
        GeminiQuotaGate quotaGate,
        params string[] apiKeys) =>
        new(
            new HandlerHttpClientFactory(handler),
            new StaticOptionsMonitor<AiSettings>(new AiSettings
            {
                Provider = AiProvider.Gemini,
                GeminiApiKeys = apiKeys.ToList(),
                GeminiModel = "gemini-2.5-flash",
                EnableWebSearch = true
            }),
            quotaGate,
            NullLogger<GeminiAiVerifier>.Instance);

    [Fact]
    public async Task A_normal_response_is_parsed_into_a_verdict_and_reaches_the_caller()
    {
        var handler = new ScriptedGeminiHandler(
            (HttpStatusCode.OK, GeminiEnvelope(VerdictJson("catalog-1", true, "Safe"))));
        var verifier = BuildVerifier(handler, new GeminiQuotaGate(), "key-1");

        verifier.IsConfigured.Should().BeTrue();
        var verdicts = await verifier.VerifyAsync(new[] { Request() });

        verdicts.Should().ContainKey("catalog-1");
        var verdict = verdicts["catalog-1"];
        verdict.IsGenuinelyNewer.Should().BeTrue();
        verdict.Risk.Should().Be(AiRiskLevel.Safe);
        verdict.LatestKnownVersion.Should().Be("32.0.15.6094");
        verdict.LatestKnownDate.Should().Be(new DateOnly(2024, 7, 15));
        verdict.RecommendedVersion.Should().Be("32.0.15.6094");
        verdict.AdvisorNote.Should().Be("Safe to install.");

        handler.Requests.Should().ContainSingle();
        handler.Requests[0].ApiKey.Should().Be("key-1");
        handler.Requests[0].Uri.Should().Contain("models/gemini-2.5-flash:generateContent");
        handler.Requests[0].Body.Should().Contain("google_search", "web search is enabled in these settings");
        handler.Requests[0].Body.Should().Contain("VEN_10DE", "the model needs the exact hardware id");
        handler.Requests[0].Body.Should().NotContain("key-1", "the API key travels in a header, never in the body");
    }

    [Fact]
    public async Task An_exhausted_key_rolls_over_to_the_next_configured_key()
    {
        var handler = new ScriptedGeminiHandler(
            (HttpStatusCode.TooManyRequests, """{"error":{"message":"Quota exceeded","details":[{"violations":[{"quotaId":"GenerateRequestsPerDayPerProjectPerModel"}]}]}}"""),
            (HttpStatusCode.OK, GeminiEnvelope(VerdictJson("catalog-1", true, "Caution"))));
        var quotaGate = new GeminiQuotaGate();
        var verifier = BuildVerifier(handler, quotaGate, "key-1", "key-2");

        var verdicts = await verifier.VerifyAsync(new[] { Request() });

        verdicts.Should().ContainKey("catalog-1");
        verdicts["catalog-1"].Risk.Should().Be(AiRiskLevel.Caution);
        handler.Requests.Select(r => r.ApiKey).Should().Equal("key-1", "key-2");
        quotaGate.IsKeyBlocked("key-1").Should().BeTrue();
        quotaGate.IsKeyBlocked("key-2").Should().BeFalse();
        verifier.IsTemporarilyUnavailable.Should().BeFalse("one key is still usable");
    }

    [Fact]
    public async Task When_every_key_is_exhausted_the_verifier_reports_itself_unavailable_and_stops_calling()
    {
        var handler = new ScriptedGeminiHandler(
            (HttpStatusCode.TooManyRequests, """{"error":{"message":"Quota exceeded for requests_per_day"}}"""),
            (HttpStatusCode.TooManyRequests, """{"error":{"message":"Quota exceeded for requests_per_day"}}"""));
        var quotaGate = new GeminiQuotaGate();
        var verifier = BuildVerifier(handler, quotaGate, "key-1", "key-2");

        var quotaEvents = 0;
        quotaGate.QuotaExceeded += (_, _) => quotaEvents++;

        var first = await verifier.VerifyAsync(new[] { Request() });
        first.Should().BeEmpty();
        verifier.IsTemporarilyUnavailable.Should().BeTrue();
        quotaEvents.Should().Be(2, "each key reports its own exhaustion once");

        var second = await verifier.VerifyAsync(new[] { Request() });
        second.Should().BeEmpty();
        handler.Requests.Should().HaveCount(2, "a blocked provider must not be called again until the quota resets");
    }

    [Fact]
    public async Task A_server_error_leaves_the_scan_results_untouched_instead_of_throwing()
    {
        var handler = new ScriptedGeminiHandler(
            (HttpStatusCode.InternalServerError, """{"error":{"message":"backend error"}}"""));
        var verifier = BuildVerifier(handler, new GeminiQuotaGate(), "key-1");

        var verdicts = await verifier.VerifyAsync(new[] { Request() });

        verdicts.Should().BeEmpty();
        verifier.IsTemporarilyUnavailable.Should().BeFalse("a 500 is not a quota problem");
    }

    [Fact]
    public async Task A_response_with_no_parseable_json_yields_no_verdicts_rather_than_a_wrong_one()
    {
        var handler = new ScriptedGeminiHandler(
            (HttpStatusCode.OK, GeminiEnvelope("I could not find reliable information about this driver.")));
        var verifier = BuildVerifier(handler, new GeminiQuotaGate(), "key-1");

        var verdicts = await verifier.VerifyAsync(new[] { Request() });

        verdicts.Should().BeEmpty();
    }

    [Fact]
    public async Task Verdicts_for_a_whole_batch_come_back_keyed_by_correlation_id()
    {
        var batchAnswer = """
            {"verdicts":[
              {"id":"a","isGenuinelyNewer":true,"risk":"Safe","summary":"Recommended","rationale":"ok"},
              {"id":"b","isGenuinelyNewer":false,"risk":"HighRisk","summary":"Avoid for now","rationale":"regressions"}
            ]}
            """;
        var handler = new ScriptedGeminiHandler((HttpStatusCode.OK, GeminiEnvelope(batchAnswer)));
        var verifier = BuildVerifier(handler, new GeminiQuotaGate(), "key-1");

        var verdicts = await verifier.VerifyAsync(new[] { Request("a"), Request("b") });

        verdicts.Should().HaveCount(2);
        verdicts["a"].IsGenuinelyNewer.Should().BeTrue();
        verdicts["b"].IsGenuinelyNewer.Should().BeFalse();
        verdicts["b"].Risk.Should().Be(AiRiskLevel.HighRisk);
        handler.Requests.Should().ContainSingle("a batch is one request, not one per driver");
    }

    [Fact]
    public void The_daily_quota_block_lasts_until_the_next_pacific_midnight()
    {
        var quotaGate = new GeminiQuotaGate();
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);

        quotaGate.RecordTooManyRequests(
            "key-1",
            response,
            """{"error":{"details":[{"violations":[{"quotaId":"GenerateRequestsPerDayPerProjectPerModel"}]}]}}""");

        quotaGate.IsKeyBlocked("key-1").Should().BeTrue();
        quotaGate.TryGetBlockedMessage("key-1", out var message).Should().BeTrue();
        message.Should().Contain("daily");

        var reset = GeminiQuotaGate.GetNextDailyResetUtc(DateTimeOffset.UtcNow);
        reset.Should().BeAfter(DateTimeOffset.UtcNow);
        reset.Should().BeBefore(DateTimeOffset.UtcNow.AddHours(25));
    }

    [Fact]
    public void A_short_lived_rate_limit_uses_the_retry_delay_the_server_returned()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero));
        var quotaGate = new GeminiQuotaGate(clock);
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);

        quotaGate.RecordTooManyRequests("key-1", response, """{"error":{"details":[{"retryDelay":"45s"}]}}""");

        quotaGate.IsKeyBlocked("key-1").Should().BeTrue();
        clock.Advance(TimeSpan.FromSeconds(44));
        quotaGate.IsKeyBlocked("key-1").Should().BeTrue();
        clock.Advance(TimeSpan.FromSeconds(2));
        quotaGate.IsKeyBlocked("key-1").Should().BeFalse("the server said to retry after 45 seconds");
    }

    private sealed class HandlerHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public HandlerHttpClientFactory(HttpMessageHandler handler) => _handler = handler;

        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private sealed class ScriptedGeminiHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body)> _responses;

        public ScriptedGeminiHandler(params (HttpStatusCode Status, string Body)[] responses) =>
            _responses = new Queue<(HttpStatusCode, string)>(responses);

        public List<(string Uri, string? ApiKey, string Body)> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((
                request.RequestUri!.ToString(),
                request.Headers.TryGetValues("x-goog-api-key", out var values) ? values.FirstOrDefault() : null,
                body));

            var (status, responseBody) = _responses.Count > 0
                ? _responses.Dequeue()
                : (HttpStatusCode.ServiceUnavailable, "{}");
            return new HttpResponseMessage(status) { Content = new StringContent(responseBody) };
        }
    }
}
