using DriverUpdater.Core.Models;
using DriverUpdater.Core.Options;
using DriverUpdater.Services.Ai;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriverUpdater.Services.Tests.Ai;

public class OllamaAiVerifierTests
{
    [Fact]
    public void IsConfigured_is_false_when_provider_is_not_ollama()
    {
        var verifier = NewVerifier(
            new AiSettings { Provider = AiProvider.Gemini },
            new CapturingHandler(""));

        verifier.IsConfigured.Should().BeFalse();
    }

    [Fact]
    public void IsConfigured_is_true_for_ollama_with_url_and_model()
    {
        var verifier = NewVerifier(
            new AiSettings { Provider = AiProvider.Ollama, OllamaBaseUrl = "http://localhost:11434", OllamaModel = "llama3.1" },
            new CapturingHandler(""));

        verifier.IsConfigured.Should().BeTrue();
    }

    [Fact]
    public async Task VerifyAsync_posts_json_format_to_chat_endpoint_and_parses_message_content()
    {
        var handler = new CapturingHandler(CannedResponse("corr-9", false, "Safe"));
        var verifier = NewVerifier(
            new AiSettings
            {
                Provider = AiProvider.Ollama,
                OllamaBaseUrl = "http://localhost:11434/",
                OllamaModel = "llama3.1"
            },
            handler);

        var result = await verifier.VerifyAsync(new[] { NewRequest("corr-9") });

        handler.LastRequest!.RequestUri!.AbsoluteUri.Should().Be("http://localhost:11434/api/chat");
        handler.LastRequestBody.Should().Contain("\"format\":\"json\"");
        handler.LastRequestBody.Should().Contain("llama3.1");
        result.Should().ContainKey("corr-9");
        result["corr-9"].IsGenuinelyNewer.Should().BeFalse();
    }

    [Fact]
    public async Task VerifyAsync_returns_empty_on_network_failure()
    {
        var verifier = NewVerifier(
            new AiSettings { Provider = AiProvider.Ollama, OllamaBaseUrl = "http://localhost:11434", OllamaModel = "llama3.1" },
            new ThrowingHandler());

        var result = await verifier.VerifyAsync(new[] { NewRequest("corr-9") });

        result.Should().BeEmpty();
    }

    private static OllamaAiVerifier NewVerifier(AiSettings settings, HttpMessageHandler handler) =>
        new(new SingleClientHttpClientFactory(handler),
            AiTestSettings.Monitor(settings),
            NullLogger<OllamaAiVerifier>.Instance);

    private static string CannedResponse(string id, bool genuinelyNewer, string risk) =>
        AiResponses.Ollama(AiResponses.VerdictsText(id, genuinelyNewer, risk, latest: null));

    private static AiVerificationRequest NewRequest(string correlationId) => new(
        CorrelationId: correlationId,
        DeviceName: "Realtek Audio",
        HardwareId: "PCI\\VEN_10EC&DEV_8168",
        InstalledVersion: "6.0.9927.1",
        InstalledDate: new DateOnly(2024, 1, 1),
        CandidateVersion: "6.0.9927.1",
        CandidateDate: new DateOnly(2026, 1, 1),
        Source: UpdateSource.Oem,
        DownloadUrl: "https://example.com/audio.exe");
}
