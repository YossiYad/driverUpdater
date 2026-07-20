using DriverUpdater.Core.Models;
using DriverUpdater.Core.Options;
using DriverUpdater.Services.Ai;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriverUpdater.Services.Tests.Ai;

public class GeminiAiVerifierTests
{
    [Fact]
    public void IsConfigured_is_false_without_an_api_key()
    {
        var verifier = NewVerifier(
            new AiSettings { Provider = AiProvider.Gemini, GeminiApiKey = "" },
            new CapturingHandler(""));

        verifier.IsConfigured.Should().BeFalse();
    }

    [Fact]
    public void IsConfigured_is_false_when_provider_is_not_gemini()
    {
        var verifier = NewVerifier(
            new AiSettings { Provider = AiProvider.Ollama, GeminiApiKey = "key" },
            new CapturingHandler(""));

        verifier.IsConfigured.Should().BeFalse();
    }

    [Fact]
    public async Task VerifyAsync_sends_api_key_header_and_google_search_tool_when_web_search_enabled()
    {
        var handler = new CapturingHandler(CannedResponse("corr-1", true, "Safe"));
        var verifier = NewVerifier(
            new AiSettings
            {
                Provider = AiProvider.Gemini,
                GeminiApiKey = "secret-key",
                GeminiModel = "gemini-2.0-flash",
                EnableWebSearch = true
            },
            handler);

        var result = await verifier.VerifyAsync(new[] { NewRequest("corr-1") });

        handler.LastRequest!.RequestUri!.AbsoluteUri.Should()
            .Be("https://generativelanguage.googleapis.com/v1beta/models/gemini-2.0-flash:generateContent");
        handler.LastRequest.Headers.GetValues("x-goog-api-key").Should().ContainSingle().Which.Should().Be("secret-key");
        handler.LastRequestBody.Should().Contain("google_search");
        result.Should().ContainKey("corr-1");
        result["corr-1"].IsGenuinelyNewer.Should().BeTrue();
        result["corr-1"].Risk.Should().Be(AiRiskLevel.Safe);
    }

    [Fact]
    public async Task VerifyAsync_omits_tools_when_web_search_disabled()
    {
        var handler = new CapturingHandler(CannedResponse("corr-1", false, "Caution"));
        var verifier = NewVerifier(
            new AiSettings
            {
                Provider = AiProvider.Gemini,
                GeminiApiKey = "k",
                EnableWebSearch = false
            },
            handler);

        await verifier.VerifyAsync(new[] { NewRequest("corr-1") });

        handler.LastRequestBody.Should().NotContain("google_search");
    }

    [Fact]
    public async Task VerifyAsync_returns_empty_on_network_failure()
    {
        var verifier = NewVerifier(
            new AiSettings { Provider = AiProvider.Gemini, GeminiApiKey = "k" },
            new ThrowingHandler());

        var result = await verifier.VerifyAsync(new[] { NewRequest("corr-1") });

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task VerifyAsync_returns_empty_when_http_request_times_out()
    {
        var verifier = NewVerifier(
            new AiSettings { Provider = AiProvider.Gemini, GeminiApiKey = "k" },
            new TimeoutHandler());

        var result = await verifier.VerifyAsync(new[] { NewRequest("corr-1") });

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task VerifyAsync_still_throws_when_the_caller_cancels()
    {
        var verifier = NewVerifier(
            new AiSettings { Provider = AiProvider.Gemini, GeminiApiKey = "k" },
            new CallerCancellationHandler());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var act = () => verifier.VerifyAsync(new[] { NewRequest("corr-1") }, cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task VerifyAsync_returns_empty_on_http_error_status()
    {
        var handler = new CapturingHandler(
            "Quota exceeded: GenerateRequestsPerDay",
            System.Net.HttpStatusCode.TooManyRequests);
        var verifier = NewVerifier(
            new AiSettings { Provider = AiProvider.Gemini, GeminiApiKey = "k" },
            handler);

        var first = await verifier.VerifyAsync(new[] { NewRequest("corr-1") });
        var second = await verifier.VerifyAsync(new[] { NewRequest("corr-1") });

        first.Should().BeEmpty();
        second.Should().BeEmpty();
        handler.RequestCount.Should().Be(1, "the quota gate should stop repeated requests until reset");
    }

    [Fact]
    public async Task VerifyAsync_returns_empty_without_calling_when_not_configured()
    {
        var handler = new CapturingHandler(CannedResponse("corr-1", true, "Safe"));
        var verifier = NewVerifier(
            new AiSettings { Provider = AiProvider.Gemini, GeminiApiKey = "" },
            handler);

        var result = await verifier.VerifyAsync(new[] { NewRequest("corr-1") });

        result.Should().BeEmpty();
        handler.LastRequest.Should().BeNull();
    }

    private static GeminiAiVerifier NewVerifier(AiSettings settings, HttpMessageHandler handler) =>
        new(new SingleClientHttpClientFactory(handler),
            AiTestSettings.Monitor(settings),
            new GeminiQuotaGate(),
            NullLogger<GeminiAiVerifier>.Instance);

    private static string CannedResponse(string id, bool genuinelyNewer, string risk) =>
        AiResponses.Gemini(AiResponses.VerdictsText(id, genuinelyNewer, risk));

    private static AiVerificationRequest NewRequest(string correlationId) => new(
        CorrelationId: correlationId,
        DeviceName: "AMD Radeon RX 7700 XT",
        HardwareId: "PCI\\VEN_1002&DEV_747E",
        InstalledVersion: "1.0.0.0",
        InstalledDate: new DateOnly(2024, 1, 1),
        CandidateVersion: "2.0.0.0",
        CandidateDate: new DateOnly(2026, 1, 1),
        Source: UpdateSource.Oem,
        DownloadUrl: "https://example.com/driver.exe");

    private sealed class TimeoutHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new TaskCanceledException("The request timed out.");
    }

    private sealed class CallerCancellationHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromCanceled<HttpResponseMessage>(cancellationToken);
    }
}
