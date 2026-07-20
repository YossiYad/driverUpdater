using System.Diagnostics;
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
    private readonly GeminiQuotaGate _quotaGate;
    private readonly ILogger<GeminiAiVerifier> _logger;

    public GeminiAiVerifier(
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<AiSettings> settings,
        GeminiQuotaGate quotaGate,
        ILogger<GeminiAiVerifier> logger)
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

    public bool IsTemporarilyUnavailable => _quotaGate.IsBlocked;

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
        if (_quotaGate.TryGetBlockedMessage(out var quotaMessage))
        {
            _logger.LogWarning("Gemini verification skipped: {Reason}", quotaMessage);
            return empty;
        }

        var settings = _settings.CurrentValue;
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{settings.GeminiModel}:generateContent";
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var prompt = AiVerificationProtocol.BuildPrompt(requests);
            var payload = BuildPayload(prompt, settings.EnableWebSearch);

            _logger.LogInformation(
                "Gemini verification starting: model={Model}, webSearch={WebSearch}, candidates={Count}, endpoint={Url}",
                settings.GeminiModel, settings.EnableWebSearch, requests.Count, url);
            _logger.LogDebug("Gemini prompt ({Length} chars):{NewLine}{Prompt}", prompt.Length, Environment.NewLine, prompt);
            _logger.LogDebug("Gemini request payload (api key sent via header, not logged):{NewLine}{Payload}",
                Environment.NewLine, SerializePayload(payload));

            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(payload)
            };
            request.Headers.Add("x-goog-api-key", settings.GeminiApiKey);

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            _logger.LogInformation(
                "Gemini HTTP {Status} ({StatusText}) in {ElapsedMs} ms",
                (int)response.StatusCode, response.StatusCode, stopwatch.ElapsedMilliseconds);

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    _quotaGate.RecordTooManyRequests(response, json);
                }
                _logger.LogWarning(
                    "Gemini verification failed: HTTP {Status} after {ElapsedMs} ms. Body: {Body}",
                    (int)response.StatusCode, stopwatch.ElapsedMilliseconds, Truncate(json, 2000));
                return empty;
            }

            _logger.LogDebug("Gemini raw response ({Length} chars):{NewLine}{Body}",
                json.Length, Environment.NewLine, Truncate(json, 8000));

            var text = ExtractText(json);
            _logger.LogDebug("Gemini extracted model text ({Length} chars):{NewLine}{Text}",
                text?.Length ?? 0, Environment.NewLine, text ?? "(none)");

            var verdicts = AiVerificationProtocol.ParseVerdicts(text);
            if (verdicts.Count == 0)
            {
                _logger.LogWarning(
                    "Gemini returned HTTP 200 but no verdicts could be parsed from the response (model text length {Length}). " +
                    "The model output is logged at Debug above.", text?.Length ?? 0);
            }
            else
            {
                _logger.LogInformation(
                    "Gemini returned {Count} verdicts for {Requested} requests in {ElapsedMs} ms",
                    verdicts.Count, requests.Count, stopwatch.ElapsedMilliseconds);
                LogVerdicts(_logger, "Gemini", verdicts);
            }
            return verdicts;
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex,
                "Gemini verification timed out after {ElapsedMs} ms; skipping this batch so the scan can continue",
                stopwatch.ElapsedMilliseconds);
            return empty;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation(
                "Gemini verification cancelled by the user after {ElapsedMs} ms",
                stopwatch.ElapsedMilliseconds);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Gemini verification failed after {ElapsedMs} ms; skipping AI verification (scan continues unchanged)",
                stopwatch.ElapsedMilliseconds);
            return empty;
        }
    }

    private static string SerializePayload(object payload)
    {
        try
        {
            return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception)
        {
            return "(payload could not be serialized for logging)";
        }
    }

    internal static void LogVerdicts(ILogger logger, string provider, IReadOnlyDictionary<string, AiVerdict> verdicts)
    {
        foreach (var (id, verdict) in verdicts)
        {
            logger.LogInformation(
                "{Provider} verdict {Id}: genuinelyNewer={GenuinelyNewer}, risk={Risk}, latestKnown={Latest}, recommended={Recommended}, summary={Summary}",
                provider, id, verdict.IsGenuinelyNewer, verdict.Risk,
                verdict.LatestKnownVersion ?? "(none)",
                verdict.RecommendedVersion ?? "(none)",
                verdict.Summary);
            logger.LogDebug(
                "{Provider} advisor {Id}: installedSuitability={InstalledSuitability}; candidateSuitability={CandidateSuitability}; advisorNote={AdvisorNote}; latestDate={LatestDate}; latestUrl={LatestUrl}; rationale={Rationale}",
                provider, id,
                verdict.InstalledSuitability ?? "(none)",
                verdict.CandidateSuitability ?? "(none)",
                verdict.AdvisorNote ?? "(none)",
                verdict.LatestKnownDate?.ToString("yyyy-MM-dd") ?? "(none)",
                verdict.LatestKnownUrl ?? "(none)",
                verdict.Rationale);
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

    internal static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + $"... (+{value.Length - maxLength} more chars)";
}
