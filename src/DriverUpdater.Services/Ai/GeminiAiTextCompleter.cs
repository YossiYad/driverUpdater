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
        && !string.IsNullOrWhiteSpace(_settings.CurrentValue.GeminiApiKey);

    public async Task<string?> CompleteAsync(string prompt, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        if (!IsConfigured)
        {
            return null;
        }
        if (_quotaGate.TryGetBlockedMessage(out var quotaMessage))
        {
            throw new AiTextCompletionException(
                AiTextCompletionFailureReason.QuotaExceeded,
                quotaMessage);
        }

        var settings = _settings.CurrentValue;
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

        _logger.LogInformation("Gemini text completion starting: model={Model}, promptChars={Chars}",
            settings.GeminiModel, prompt.Length);

        var client = _httpClientFactory.CreateClient(GeminiAiVerifier.HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(payload)
        };
        request.Headers.Add("x-goog-api-key", settings.GeminiApiKey);

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Gemini text completion failed: HTTP {Status} after {ElapsedMs} ms. Body: {Body}",
                (int)response.StatusCode, stopwatch.ElapsedMilliseconds, GeminiAiVerifier.Truncate(json, 2000));
            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                _quotaGate.RecordTooManyRequests(response, json);
                _quotaGate.TryGetBlockedMessage(out var message);
                throw new AiTextCompletionException(
                    AiTextCompletionFailureReason.QuotaExceeded,
                    message);
            }
            return null;
        }

        var text = ExtractText(json);
        _logger.LogInformation("Gemini text completion returned {Chars} chars in {ElapsedMs} ms",
            text?.Length ?? 0, stopwatch.ElapsedMilliseconds);
        return text;
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
