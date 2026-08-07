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
    public async Task CompleteAsync_declares_the_google_search_tool_when_web_search_is_enabled()
    {
        var handler = new CapturingHandler(AiResponses.Gemini("answer"));
        var completer = NewCompleter(handler, enableWebSearch: true);

        var result = await completer.CompleteAsync("What driver version is recommended?");

        result.Should().Be("answer");
        handler.LastRequestBody.Should().Contain("google_search");
    }

    [Fact]
    public async Task CompleteAsync_omits_the_google_search_tool_when_web_search_is_disabled()
    {
        var handler = new CapturingHandler(AiResponses.Gemini("answer"));
        var completer = NewCompleter(handler, enableWebSearch: false);

        await completer.CompleteAsync("What driver version is recommended?");

        handler.LastRequestBody.Should().NotContain("google_search");
    }

    [Fact]
    public async Task CompleteAsync_appends_deduplicated_grounding_source_titles()
    {
        const string body = "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"Use version 552.22.\"}]}," +
            "\"groundingMetadata\":{\"groundingChunks\":[" +
            "{\"web\":{\"uri\":\"https://r/1\",\"title\":\"nvidia.com\"}}," +
            "{\"web\":{\"uri\":\"https://r/2\",\"title\":\"reddit.com\"}}," +
            "{\"web\":{\"uri\":\"https://r/3\",\"title\":\"NVIDIA.com\"}}]}}]}";
        var handler = new CapturingHandler(body);
        var completer = NewCompleter(handler, enableWebSearch: true);

        var result = await completer.CompleteAsync("What driver version is recommended?");

        result.Should().Be("Use version 552.22.\n\nSources: nvidia.com, reddit.com");
    }

    [Fact]
    public async Task CompleteAsync_returns_plain_text_when_no_grounding_metadata_is_present()
    {
        var handler = new CapturingHandler(AiResponses.Gemini("plain answer"));
        var completer = NewCompleter(handler, enableWebSearch: true);

        var result = await completer.CompleteAsync("hello");

        result.Should().Be("plain answer");
    }

    private static GeminiAiTextCompleter NewCompleter(CapturingHandler handler, bool enableWebSearch) =>
        new(new SingleClientHttpClientFactory(handler),
            AiTestSettings.Monitor(new AiSettings
            {
                Provider = AiProvider.Gemini,
                GeminiApiKey = "key",
                EnableWebSearch = enableWebSearch
            }),
            new GeminiQuotaGate(),
            NullLogger<GeminiAiTextCompleter>.Instance);

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
