using System.Net;
using DriverUpdater.Core.Models;
using DriverUpdater.Core.Options;
using DriverUpdater.Services.Ai;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriverUpdater.Services.Tests.Ai;

public class GeminiAiTextCompleterTests
{
    [Fact]
    public async Task CompleteAsync_falls_back_to_the_next_key_after_quota_exhaustion()
    {
        var handler = new SequenceHandler(
            (HttpStatusCode.TooManyRequests, "Quota exceeded: GenerateRequestsPerDay"),
            (HttpStatusCode.OK, AiResponses.Gemini("completed")));
        var completer = new GeminiAiTextCompleter(
            new SingleClientHttpClientFactory(handler),
            AiTestSettings.Monitor(new AiSettings
            {
                Provider = AiProvider.Gemini,
                GeminiApiKeys = new List<string> { "first-key", "second-key" }
            }),
            new GeminiQuotaGate(),
            NullLogger<GeminiAiTextCompleter>.Instance);

        var result = await completer.CompleteAsync("Summarize these logs.");

        result.Should().Be("completed");
        handler.ApiKeys.Should().Equal("first-key", "second-key");
    }

    [Fact]
    public async Task CompleteAsync_reports_quota_exhaustion_and_blocks_repeated_requests()
    {
        var handler = new CapturingHandler(
            "Quota exceeded: GenerateRequestsPerDay",
            HttpStatusCode.TooManyRequests);
        var completer = new GeminiAiTextCompleter(
            new SingleClientHttpClientFactory(handler),
            AiTestSettings.Monitor(new AiSettings
            {
                Provider = AiProvider.Gemini,
                GeminiApiKey = "key"
            }),
            new GeminiQuotaGate(),
            NullLogger<GeminiAiTextCompleter>.Instance);

        var first = () => completer.CompleteAsync("Summarize these logs.");
        var second = () => completer.CompleteAsync("Summarize these logs again.");

        var firstError = await first.Should().ThrowAsync<AiTextCompletionException>();
        firstError.Which.Reason.Should().Be(AiTextCompletionFailureReason.QuotaExceeded);
        firstError.Which.Message.Should().Contain("daily request quota");
        await second.Should().ThrowAsync<AiTextCompletionException>();
        handler.RequestCount.Should().Be(1);
    }
}
