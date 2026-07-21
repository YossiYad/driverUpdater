using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using DriverUpdater.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DriverUpdater.Services.Ai;

public sealed class GeminiAiTextCompleter : IAiTextCompleter
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptionsMonitor<AiSettings> _settings;
    private readonly GeminiQuotaGate _quotaGate;
    private readonly ILogger<GeminiAiTextCompleter> _logger;

    public GeminiAiTextCompleter(
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<AiSettings> settings,
        GeminiQuotaGate quotaGate,
        ILogger<GeminiAiTextCompleter> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(quotaGate);
        ArgumentNullException.ThrowIfNull(logger);
        _httpClientFactory = httpClientFactory;
        _settings = settings;
        _quotaGate = quotaGate;
        _logger = logger;
    }

    public AiProvider Provider => AiProvider.Gemini;

    public bool IsConfigured =>
        _settings.CurrentValue.Provider == AiProvider.Gemini
        && _settings.CurrentValue.GetGeminiApiKeys().Count > 0;

    public async Task<string?> CompleteAsync(string prompt, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        if (!IsConfigured)
        {
            return null;
        }
        var settings = _settings.CurrentValue;
        var apiKeys = settings.GetGeminiApiKeys();
        if (_quotaGate.AreAllBlocked(apiKeys))
        {
            _quotaGate.TryGetBlockedMessage(apiKeys[0], out var quotaMessage);
            throw new AiTextCompletionException(
                AiTextCompletionFailureReason.QuotaExceeded,
                quotaMessage);
        }

        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{settings.GeminiModel}:generateContent";
        var stopwatch = Stopwatch.StartNew();

        var payload = new
        {
            contents = new[]
            {
                new { role = "user", parts = new[] { new { text = prompt } } }
            },
            generationConfig = new { temperature = 0.2 }
        };

        _logger.LogInformation(
            "Gemini text completion starting: model={Model}, promptChars={Chars}, apiKeys={KeyCount}",
            settings.GeminiModel,
            prompt.Length,
            apiKeys.Count);

        var client = _httpClientFactory.CreateClient(GeminiAiVerifier.HttpClientName);
        for (var keyIndex = 0; keyIndex < apiKeys.Count; keyIndex++)
        {
            var apiKey = apiKeys[keyIndex];
            if (_quotaGate.TryGetBlockedMessage(apiKey, out var blockedMessage))
            {
                _logger.LogInformation(
                    "Gemini API key {KeyNumber} of {KeyCount} is still unavailable and was skipped: {Reason}",
                    keyIndex + 1,
                    apiKeys.Count,
                    blockedMessage);
                continue;
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(payload)
            };
            request.Headers.Add("x-goog-api-key", apiKey);

            _logger.LogInformation(
                "Trying Gemini API key {KeyNumber} of {KeyCount} for text completion",
                keyIndex + 1,
                apiKeys.Count);
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Gemini text completion failed: HTTP {Status} using API key {KeyNumber} of {KeyCount} after {ElapsedMs} ms. Body: {Body}",
                    (int)response.StatusCode,
                    keyIndex + 1,
                    apiKeys.Count,
                    stopwatch.ElapsedMilliseconds,
                    GeminiAiVerifier.Truncate(json, 2000));
                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    _quotaGate.RecordTooManyRequests(apiKey, response, json);
                    _logger.LogWarning(
                        "Gemini API key {KeyNumber} of {KeyCount} reached its quota. Falling back to the next configured key.",
                        keyIndex + 1,
                        apiKeys.Count);
                    continue;
                }
                return null;
            }

            stopwatch.Stop();
            var text = ExtractText(json);
            _logger.LogInformation(
                "Gemini text completion returned {Chars} chars using API key {KeyNumber} of {KeyCount} in {ElapsedMs} ms",
                text?.Length ?? 0,
                keyIndex + 1,
                apiKeys.Count,
                stopwatch.ElapsedMilliseconds);
            return text;
        }

        stopwatch.Stop();
        var message = apiKeys
            .Select(key => _quotaGate.TryGetBlockedMessage(key, out var reason) ? reason : null)
            .FirstOrDefault(reason => !string.IsNullOrWhiteSpace(reason))
            ?? "All configured Gemini API keys are temporarily unavailable.";
        _logger.LogWarning(
            "Gemini text completion could not run because all {KeyCount} configured API keys reached their quota",
            apiKeys.Count);
        throw new AiTextCompletionException(
            AiTextCompletionFailureReason.QuotaExceeded,
            message);
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

            var sb = new StringBuilder();
            foreach (var part in parts.EnumerateArray())
            {
                if (part.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                {
                    sb.Append(t.GetString());
                }
            }
            return sb.Length == 0 ? null : sb.ToString();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
