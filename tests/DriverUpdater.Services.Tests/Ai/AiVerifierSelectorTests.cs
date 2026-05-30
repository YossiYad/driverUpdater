using DriverUpdater.Core.Models;
using DriverUpdater.Core.Options;
using DriverUpdater.Services.Ai;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriverUpdater.Services.Tests.Ai;

public class AiVerifierSelectorTests
{
    [Fact]
    public async Task Off_provider_is_not_configured_and_returns_empty()
    {
        var settings = AiTestSettings.Monitor(new AiSettings { Provider = AiProvider.Off });
        var selector = NewSelector(settings, geminiHandler: new ThrowingHandler(), ollamaHandler: new ThrowingHandler());

        selector.Provider.Should().Be(AiProvider.Off);
        selector.IsConfigured.Should().BeFalse();
        (await selector.VerifyAsync(new[] { NewRequest("c1") })).Should().BeEmpty();
    }

    [Fact]
    public void Gemini_without_key_is_not_configured()
    {
        var settings = AiTestSettings.Monitor(new AiSettings { Provider = AiProvider.Gemini, GeminiApiKey = "" });
        var selector = NewSelector(settings, new CapturingHandler(""), new CapturingHandler(""));

        selector.Provider.Should().Be(AiProvider.Gemini);
        selector.IsConfigured.Should().BeFalse();
    }

    [Fact]
    public async Task Gemini_provider_routes_to_gemini_endpoint()
    {
        var settings = AiTestSettings.Monitor(new AiSettings
        {
            Provider = AiProvider.Gemini,
            GeminiApiKey = "k",
            GeminiModel = "gemini-2.0-flash"
        });
        var geminiHandler = new CapturingHandler(GeminiResponse("c1"));
        var ollamaHandler = new ThrowingHandler();
        var selector = NewSelector(settings, geminiHandler, ollamaHandler);

        selector.IsConfigured.Should().BeTrue();
        var result = await selector.VerifyAsync(new[] { NewRequest("c1") });

        result.Should().ContainKey("c1");
        geminiHandler.LastRequest!.RequestUri!.AbsoluteUri.Should().Contain("generativelanguage.googleapis.com");
    }

    [Fact]
    public async Task Ollama_provider_routes_to_ollama_endpoint()
    {
        var settings = AiTestSettings.Monitor(new AiSettings
        {
            Provider = AiProvider.Ollama,
            OllamaBaseUrl = "http://localhost:11434",
            OllamaModel = "llama3.1"
        });
        var ollamaHandler = new CapturingHandler(OllamaResponse("c1"));
        var selector = NewSelector(settings, new ThrowingHandler(), ollamaHandler);

        selector.IsConfigured.Should().BeTrue();
        var result = await selector.VerifyAsync(new[] { NewRequest("c1") });

        result.Should().ContainKey("c1");
        ollamaHandler.LastRequest!.RequestUri!.AbsoluteUri.Should().Be("http://localhost:11434/api/chat");
    }

    private static AiVerifierSelector NewSelector(
        StaticOptionsMonitor<AiSettings> settings,
        HttpMessageHandler geminiHandler,
        HttpMessageHandler ollamaHandler)
    {
        var gemini = new GeminiAiVerifier(
            new SingleClientHttpClientFactory(geminiHandler), settings, NullLogger<GeminiAiVerifier>.Instance);
        var ollama = new OllamaAiVerifier(
            new SingleClientHttpClientFactory(ollamaHandler), settings, NullLogger<OllamaAiVerifier>.Instance);
        return new AiVerifierSelector(gemini, ollama, settings);
    }

    private static string GeminiResponse(string id) =>
        AiResponses.Gemini(AiResponses.VerdictsText(id, genuinelyNewer: true, risk: "Safe"));

    private static string OllamaResponse(string id) =>
        AiResponses.Ollama(AiResponses.VerdictsText(id, genuinelyNewer: true, risk: "Safe"));

    private static AiVerificationRequest NewRequest(string correlationId) => new(
        CorrelationId: correlationId,
        DeviceName: "Device",
        HardwareId: "HW\\X",
        InstalledVersion: "1.0.0.0",
        InstalledDate: null,
        CandidateVersion: "2.0.0.0",
        CandidateDate: new DateOnly(2026, 1, 1),
        Source: UpdateSource.WindowsUpdate,
        DownloadUrl: "https://example.com/x");
}
