using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using DriverUpdater.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DriverUpdater.Services.Ai;

// The single IAiVerifier the app consumes. It reads the configured provider at call time
// (settings can change at runtime via the Settings window) and delegates to the matching
// concrete verifier, or no-ops when AI is Off.
public sealed class AiVerifierSelector : IAiVerifier
{
    private static readonly IReadOnlyDictionary<string, AiVerdict> Empty =
        new Dictionary<string, AiVerdict>();

    private readonly GeminiAiVerifier _gemini;
    private readonly OllamaAiVerifier _ollama;
    private readonly IOptionsMonitor<AiSettings> _settings;
    private readonly ILogger<AiVerifierSelector>? _logger;

    public AiVerifierSelector(
        GeminiAiVerifier gemini,
        OllamaAiVerifier ollama,
        IOptionsMonitor<AiSettings> settings,
        ILogger<AiVerifierSelector>? logger = null)
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

    public bool IsTemporarilyUnavailable => Current()?.IsTemporarilyUnavailable ?? false;

    public Task<IReadOnlyDictionary<string, AiVerdict>> VerifyAsync(
        IReadOnlyList<AiVerificationRequest> requests,
        CancellationToken cancellationToken = default)
    {
        var provider = _settings.CurrentValue.Provider;
        var verifier = Current();
        if (verifier is null)
        {
            _logger?.LogDebug("AI verification skipped: provider is {Provider}", provider);
            return Task.FromResult(Empty);
        }
        if (!verifier.IsConfigured)
        {
            _logger?.LogWarning(
                "AI verification skipped: provider {Provider} is selected but not fully configured", provider);
            return Task.FromResult(Empty);
        }

        _logger?.LogInformation(
            "AI request routed to {Provider}: total={Count}, discovery={DiscoveryCount}, candidateVerification={CandidateCount}",
            provider,
            requests.Count,
            requests.Count(r => r.FindLatestWhenNoCandidate),
            requests.Count(r => !r.FindLatestWhenNoCandidate));
        return verifier.VerifyAsync(requests, cancellationToken);
    }

    private IAiVerifier? Current() => _settings.CurrentValue.Provider switch
    {
        AiProvider.Gemini => _gemini,
        AiProvider.Ollama => _ollama,
        _ => null
    };
}
