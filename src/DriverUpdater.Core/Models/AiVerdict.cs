namespace DriverUpdater.Core.Models;

public sealed record AiVerdict(
    bool IsGenuinelyNewer,
    AiRiskLevel Risk,
    string Summary,
    string Rationale,
    string? LatestKnownVersion,
    DateOnly? LatestKnownDate = null,
    string? LatestKnownUrl = null,
    string? InstalledSuitability = null,
    string? CandidateSuitability = null,
    string? RecommendedVersion = null,
    string? AdvisorNote = null);
