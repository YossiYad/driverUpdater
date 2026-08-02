using DriverUpdater.Core.Models;

namespace DriverUpdater.Core.Abstractions;

/// <summary>
/// Asks the configured AI provider which of the updates a scheduled run found are worth
/// installing unattended. Only consulted when <see cref="AutoUpdateScope.AiRecommended"/>
/// is active. A decision is made per <see cref="UpdateCandidate.SourceUpdateId"/>, so one
/// installer shared by many device rows is reviewed once.
/// </summary>
public interface IAiAutoUpdateAdvisor
{
    bool IsConfigured { get; }

    Task<IReadOnlyList<AiUpdateDecision>> ReviewAsync(
        IReadOnlyList<AiUpdateReviewItem> items,
        AiAutoUpdateRiskTolerance riskTolerance,
        CancellationToken cancellationToken = default);
}
