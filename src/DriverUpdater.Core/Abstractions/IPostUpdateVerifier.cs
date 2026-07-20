using DriverUpdater.Core.Models;

namespace DriverUpdater.Core.Abstractions;

public interface IPostUpdateVerifier
{
    Task<UpdateVerificationReport> VerifyAsync(
        PendingUpdateVerificationBatch batch,
        bool isAfterRestart,
        AppLanguage language,
        CancellationToken cancellationToken = default);
}
