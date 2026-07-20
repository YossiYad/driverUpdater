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
