using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using DriverUpdater.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DriverUpdater.Services.Ai;

// The single IAiTextCompleter the app consumes. Reads the configured provider at call time
// and delegates to the matching concrete completer, or no-ops when AI is Off.
public sealed class AiTextCompleterSelector : IAiTextCompleter
{
    private readonly GeminiAiTextCompleter _gemini;
    private readonly OllamaAiTextCompleter _ollama;
    private readonly IOptionsMonitor<AiSettings> _settings;
    private readonly ILogger<AiTextCompleterSelector>? _logger;

    public AiTextCompleterSelector(
        GeminiAiTextCompleter gemini,
        OllamaAiTextCompleter ollama,
        IOptionsMonitor<AiSettings> settings,
        ILogger<AiTextCompleterSelector>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(gemini);
        ArgumentNullException.ThrowIfNull(ollama);
        ArgumentNullException.ThrowIfNull(settings);
        _gemini = gemini;
        _ollama = ollama;
        _settings = settings;
        _logger = logger;
    }

    public AiProvider Provider => _settings.CurrentValue.Provider;

    public bool IsConfigured => Current()?.IsConfigured ?? false;

    public Task<string?> CompleteAsync(string prompt, CancellationToken cancellationToken = default)
    {
        var provider = _settings.CurrentValue.Provider;
        var completer = Current();
        if (completer is null)
        {
            _logger?.LogDebug("AI text completion skipped: provider is {Provider}", provider);
            return Task.FromResult<string?>(null);
        }
        if (!completer.IsConfigured)
        {
            _logger?.LogWarning(
                "AI text completion skipped: provider {Provider} is selected but not fully configured", provider);
            return Task.FromResult<string?>(null);
        }

        _logger?.LogInformation("AI text completion routed to {Provider}", provider);
        return completer.CompleteAsync(prompt, cancellationToken);
    }

    private IAiTextCompleter? Current() => _settings.CurrentValue.Provider switch
    {
        AiProvider.Gemini => _gemini,
        AiProvider.Ollama => _ollama,
        _ => null
    };
}
