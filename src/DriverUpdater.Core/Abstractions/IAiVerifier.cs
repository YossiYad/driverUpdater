using DriverUpdater.Core.Models;

namespace DriverUpdater.Core.Abstractions;

public interface IAiVerifier
{
    AiProvider Provider { get; }

    bool IsConfigured { get; }

    bool IsTemporarilyUnavailable { get; }

    Task<IReadOnlyDictionary<string, AiVerdict>> VerifyAsync(
        IReadOnlyList<AiVerificationRequest> requests,
        CancellationToken cancellationToken = default);
}
