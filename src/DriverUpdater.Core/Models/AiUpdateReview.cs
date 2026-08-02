namespace DriverUpdater.Core.Models;

/// <summary>
/// One update an unattended run is about to install, handed to the AI advisor for review.
/// </summary>
public sealed record AiUpdateReviewItem(DriverInfo Driver, UpdateCandidate Candidate);

/// <summary>
/// The advisor's answer for a single <see cref="UpdateCandidate.SourceUpdateId"/>. One decision
/// covers every device row sharing that installer, exactly like the install pass itself.
/// </summary>
public sealed record AiUpdateDecision(
    string SourceUpdateId,
    string DeviceName,
    bool ShouldInstall,
    string Reason,
    AiVerdict? Verdict = null);
