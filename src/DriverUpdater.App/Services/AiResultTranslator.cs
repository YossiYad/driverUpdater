using System.Text.Json;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using Microsoft.Extensions.Logging;

namespace DriverUpdater.App.Services;

public sealed class AiResultTranslator : IAiResultTranslator
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IAiTextCompleter _aiCompleter;
    private readonly ILogger<AiResultTranslator> _logger;

    public AiResultTranslator(
        IAiTextCompleter aiCompleter,
        ILogger<AiResultTranslator> logger)
    {
        ArgumentNullException.ThrowIfNull(aiCompleter);
        ArgumentNullException.ThrowIfNull(logger);
        _aiCompleter = aiCompleter;
        _logger = logger;
    }

    public bool IsConfigured => _aiCompleter.IsConfigured;

    public async Task<AiResultTextContent?> TranslateAsync(
        AiResultTextContent source,
        AppLanguage targetLanguage,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (!IsConfigured)
        {
            return null;
        }

        var languageName = targetLanguage switch
        {
            AppLanguage.Hebrew => "Hebrew",
            AppLanguage.English => "English",
            _ => throw new ArgumentOutOfRangeException(
                nameof(targetLanguage),
                targetLanguage,
                "Only Hebrew and English translations are supported.")
        };

        var prompt = BuildPrompt(source, languageName);

        try
        {
            var response = await _aiCompleter.CompleteAsync(prompt, cancellationToken).ConfigureAwait(false);
            return ParseResponse(response);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AI result translation to {Language} failed", languageName);
            return null;
        }
    }

    private static string BuildPrompt(AiResultTextContent source, string languageName)
    {
        var sourceJson = JsonSerializer.Serialize(source, JsonOptions);
        return $$"""
            Translate every string value in the JSON object below into {{languageName}}.
            Return only one valid JSON object with the exact same property names.
            Preserve all facts, product names, version numbers, dates, URLs, and identifiers exactly.
            Do not add advice, explanations, Markdown, or code fences.

            {{sourceJson}}
            """;
    }

    private static AiResultTextContent? ParseResponse(string? response)
    {
        if (string.IsNullOrWhiteSpace(response))
        {
            return null;
        }

        var start = response.IndexOf('{');
        var end = response.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        try
        {
            var translated = JsonSerializer.Deserialize<AiResultTextContent>(
                response[start..(end + 1)],
                JsonOptions);

            return translated is not null && HasAllContent(translated)
                ? translated
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool HasAllContent(AiResultTextContent content) =>
        !string.IsNullOrWhiteSpace(content.Recommendation)
        && !string.IsNullOrWhiteSpace(content.Summary)
        && !string.IsNullOrWhiteSpace(content.Rationale)
        && !string.IsNullOrWhiteSpace(content.InstalledSuitability)
        && !string.IsNullOrWhiteSpace(content.CandidateSuitability)
        && !string.IsNullOrWhiteSpace(content.AdvisorNote);
}
