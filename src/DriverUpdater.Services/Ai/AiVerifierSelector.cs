using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using DriverUpdater.Core.Options;
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

    public AiVerifierSelector(
        GeminiAiVerifier gemini,
        OllamaAiVerifier ollama,
        IOptionsMonitor<AiSettings> settings)
    {
        ArgumentNullException.ThrowIfNull(gemini);
        ArgumentNullException.ThrowIfNull(ollama);
        ArgumentNullException.ThrowIfNull(settings);
        _gemini = gemini;
        _ollama = ollama;
        _settings = settings;
    }

    public AiProvider Provider => _settings.CurrentValue.Provider;

    public bool IsConfigured => Current()?.IsConfigured ?? false;

    public Task<IReadOnlyDictionary<string, AiVerdict>> VerifyAsync(
        IReadOnlyList<AiVerificationRequest> requests,
        CancellationToken cancellationToken = default)
    {
        var verifier = Current();
        if (verifier is null || !verifier.IsConfigured)
        {
            return Task.FromResult(Empty);
        }
        return verifier.VerifyAsync(requests, cancellationToken);
    }

    private IAiVerifier? Current() => _settings.CurrentValue.Provider switch
    {
        AiProvider.Gemini => _gemini,
        AiProvider.Ollama => _ollama,
        _ => null
    };
}
