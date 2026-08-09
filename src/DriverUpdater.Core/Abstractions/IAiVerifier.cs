using DriverUpdater.Core.Models;

namespace DriverUpdater.Core.Abstractions;

public interface IAiVerifier
{
    AiProvider Provider { get; }

    bool IsConfigured { get; }

    bool IsTemporarilyUnavailable { get; }

    /// <param name="unattendedRun">
    /// True when the verdicts drive an unattended scheduled install rather than a review the
    /// user is watching. The prompt holds the model to a stricter bar in that case.
    /// </param>
    Task<IReadOnlyDictionary<string, AiVerdict>> VerifyAsync(
        IReadOnlyList<AiVerificationRequest> requests,
        bool unattendedRun = false,
        CancellationToken cancellationToken = default);
}
