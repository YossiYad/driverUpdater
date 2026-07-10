using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using DriverUpdater.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DriverUpdater.Services.Ai;

public sealed class OllamaAiTextCompleter : IAiTextCompleter
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptionsMonitor<AiSettings> _settings;
    private readonly ILogger<OllamaAiTextCompleter> _logger;

    public OllamaAiTextCompleter(
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<AiSettings> settings,
        ILogger<OllamaAiTextCompleter> logger)
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

    public async Task<string?> CompleteAsync(string prompt, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        if (!IsConfigured)
        {
            return null;
        }

        var settings = _settings.CurrentValue;
        var url = $"{settings.OllamaBaseUrl.TrimEnd('/')}/api/chat";
        var stopwatch = Stopwatch.StartNew();

        var payload = new
        {
            model = settings.OllamaModel,
            stream = false,
            options = new { temperature = 0.2 },
            messages = new[]
            {
                new { role = "user", content = prompt }
            }
        };

        _logger.LogInformation("Ollama text completion starting: model={Model}, promptChars={Chars}",
            settings.OllamaModel, prompt.Length);

        var client = _httpClientFactory.CreateClient(GeminiAiVerifier.HttpClientName);
        using var response = await client.PostAsJsonAsync(url, payload, cancellationToken).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Ollama text completion failed: HTTP {Status} after {ElapsedMs} ms. Body: {Body}",
                (int)response.StatusCode, stopwatch.ElapsedMilliseconds, GeminiAiVerifier.Truncate(json, 2000));
            return null;
        }

        var text = ExtractMessageContent(json);
        _logger.LogInformation("Ollama text completion returned {Chars} chars in {ElapsedMs} ms",
            text?.Length ?? 0, stopwatch.ElapsedMilliseconds);
        return text;
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
                var s = content.GetString();
                return string.IsNullOrWhiteSpace(s) ? null : s;
            }
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
