using System.Net.Http.Json;
using System.Text.Json;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using DriverUpdater.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DriverUpdater.Services.Ai;

public sealed class OllamaAiVerifier : IAiVerifier
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptionsMonitor<AiSettings> _settings;
    private readonly ILogger<OllamaAiVerifier> _logger;

    public OllamaAiVerifier(
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<AiSettings> settings,
        ILogger<OllamaAiVerifier> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);
        _httpClientFactory = httpClientFactory;
        _settings = settings;
        _logger = logger;
    }

    public AiProvider Provider => AiProvider.Ollama;

    public bool IsConfigured =>
        _settings.CurrentValue.Provider == AiProvider.Ollama
        && !string.IsNullOrWhiteSpace(_settings.CurrentValue.OllamaBaseUrl)
        && !string.IsNullOrWhiteSpace(_settings.CurrentValue.OllamaModel);

    public async Task<IReadOnlyDictionary<string, AiVerdict>> VerifyAsync(
        IReadOnlyList<AiVerificationRequest> requests,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        var empty = (IReadOnlyDictionary<string, AiVerdict>)new Dictionary<string, AiVerdict>();
        if (requests.Count == 0 || !IsConfigured)
        {
            return empty;
        }

        var settings = _settings.CurrentValue;
        try
        {
            var prompt = AiVerificationProtocol.BuildPrompt(requests);
            var payload = new
            {
                model = settings.OllamaModel,
                stream = false,
                format = "json",
                options = new { temperature = 0.0 },
                messages = new[]
                {
                    new { role = "user", content = prompt }
                }
            };

            var client = _httpClientFactory.CreateClient(GeminiAiVerifier.HttpClientName);
            var url = $"{settings.OllamaBaseUrl.TrimEnd('/')}/api/chat";
            using var response = await client.PostAsJsonAsync(url, payload, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Ollama verification HTTP {Status}", (int)response.StatusCode);
                return empty;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var text = ExtractMessageContent(json);
            var verdicts = AiVerificationProtocol.ParseVerdicts(text);
            _logger.LogInformation("Ollama returned {Count} verdicts for {Requested} requests", verdicts.Count, requests.Count);
            return verdicts;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ollama verification failed; skipping AI verification");
            return empty;
        }
    }

    private static string? ExtractMessageContent(string responseJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            if (doc.RootElement.TryGetProperty("message", out var message)
                && message.TryGetProperty("content", out var content)
                && content.ValueKind == JsonValueKind.String)
            {
                return content.GetString();
            }
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
