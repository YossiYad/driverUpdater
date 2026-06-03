namespace DriverUpdater.Core.Models;

public sealed record AiVerdict(
    bool IsGenuinelyNewer,
    AiRiskLevel Risk,
    string Summary,
    string Rationale,
    string? LatestKnownVersion);
