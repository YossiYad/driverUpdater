using DriverUpdater.Core.Models;

namespace DriverUpdater.App.Services;

public interface IAiResultTranslator
{
    bool IsConfigured { get; }

    Task<AiResultTextContent?> TranslateAsync(
        AiResultTextContent source,
        AppLanguage targetLanguage,
        CancellationToken cancellationToken = default);
}

public sealed record AiResultTextContent(
    string Recommendation,
    string Summary,
    string Rationale,
    string InstalledSuitability,
    string CandidateSuitability,
    string AdvisorNote);
