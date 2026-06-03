using System.Diagnostics;
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
        var url = $"{settings.OllamaBaseUrl.TrimEnd('/')}/api/chat";
        var stopwatch = Stopwatch.StartNew();
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

            _logger.LogInformation(
                "Ollama verification starting: model={Model}, candidates={Count}, endpoint={Url}",
                settings.OllamaModel, requests.Count, url);
            _logger.LogDebug("Ollama prompt ({Length} chars):{NewLine}{Prompt}", prompt.Length, Environment.NewLine, prompt);

            var client = _httpClientFactory.CreateClient(GeminiAiVerifier.HttpClientName);
            using var response = await client.PostAsJsonAsync(url, payload, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            _logger.LogInformation(
                "Ollama HTTP {Status} ({StatusText}) in {ElapsedMs} ms",
                (int)response.StatusCode, response.StatusCode, stopwatch.ElapsedMilliseconds);

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Ollama verification failed: HTTP {Status} after {ElapsedMs} ms. Body: {Body}",
                    (int)response.StatusCode, stopwatch.ElapsedMilliseconds, GeminiAiVerifier.Truncate(json, 2000));
                return empty;
            }

            _logger.LogDebug("Ollama raw response ({Length} chars):{NewLine}{Body}",
                json.Length, Environment.NewLine, GeminiAiVerifier.Truncate(json, 8000));

            var text = ExtractMessageContent(json);
            _logger.LogDebug("Ollama extracted model text ({Length} chars):{NewLine}{Text}",
                text?.Length ?? 0, Environment.NewLine, text ?? "(none)");

            var verdicts = AiVerificationProtocol.ParseVerdicts(text);
            if (verdicts.Count == 0)
            {
                _logger.LogWarning(
                    "Ollama returned HTTP 200 but no verdicts could be parsed from the response (model text length {Length}). " +
                    "The model output is logged at Debug above.", text?.Length ?? 0);
            }
            else
            {
                _logger.LogInformation(
                    "Ollama returned {Count} verdicts for {Requested} requests in {ElapsedMs} ms",
                    verdicts.Count, requests.Count, stopwatch.ElapsedMilliseconds);
                GeminiAiVerifier.LogVerdicts(_logger, "Ollama", verdicts);
            }
            return verdicts;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Ollama verification cancelled after {ElapsedMs} ms", stopwatch.ElapsedMilliseconds);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Ollama verification failed after {ElapsedMs} ms; skipping AI verification (scan continues unchanged)",
                stopwatch.ElapsedMilliseconds);
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
