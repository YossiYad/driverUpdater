using System.Net.Http.Json;
using System.Text.Json;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using DriverUpdater.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DriverUpdater.Services.Ai;

public sealed class GeminiAiVerifier : IAiVerifier
{
    public const string HttpClientName = "AiVerification";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptionsMonitor<AiSettings> _settings;
    private readonly ILogger<GeminiAiVerifier> _logger;

    public GeminiAiVerifier(
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<AiSettings> settings,
        ILogger<GeminiAiVerifier> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);
        _httpClientFactory = httpClientFactory;
        _settings = settings;
        _logger = logger;
    }

    public AiProvider Provider => AiProvider.Gemini;

    public bool IsConfigured =>
        _settings.CurrentValue.Provider == AiProvider.Gemini
        && !string.IsNullOrWhiteSpace(_settings.CurrentValue.GeminiApiKey);

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
            var payload = BuildPayload(prompt, settings.EnableWebSearch);

            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"https://generativelanguage.googleapis.com/v1beta/models/{settings.GeminiModel}:generateContent")
            {
                Content = JsonContent.Create(payload)
            };
            request.Headers.Add("x-goog-api-key", settings.GeminiApiKey);

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogWarning("Gemini verification HTTP {Status}: {Body}", (int)response.StatusCode, Truncate(body));
                return empty;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var text = ExtractText(json);
            var verdicts = AiVerificationProtocol.ParseVerdicts(text);
            _logger.LogInformation("Gemini returned {Count} verdicts for {Requested} requests", verdicts.Count, requests.Count);
            return verdicts;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Gemini verification failed; skipping AI verification");
            return empty;
        }
    }

    private static object BuildPayload(string prompt, bool enableWebSearch)
    {
        var contents = new[]
        {
            new { role = "user", parts = new[] { new { text = prompt } } }
        };
        var generationConfig = new { temperature = 0.0 };

        if (enableWebSearch)
        {
            return new
            {
                contents,
                tools = new object[] { new { google_search = new { } } },
                generationConfig
            };
        }

        return new { contents, generationConfig };
    }

    private static string? ExtractText(string responseJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            if (!doc.RootElement.TryGetProperty("candidates", out var candidates) || candidates.GetArrayLength() == 0)
            {
                return null;
            }
            if (!candidates[0].TryGetProperty("content", out var content)
                || !content.TryGetProperty("parts", out var parts))
            {
                return null;
            }

            var sb = new System.Text.StringBuilder();
            foreach (var part in parts.EnumerateArray())
            {
                if (part.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                {
                    sb.Append(t.GetString());
                }
            }
            return sb.ToString();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Truncate(string value) =>
        value.Length <= 500 ? value : value[..500];
}
