using DriverUpdater.Core.Models;

namespace DriverUpdater.Core.Abstractions;

public interface IPendingUpdateVerificationStore
{
    Task SaveAsync(PendingUpdateVerificationBatch batch, CancellationToken cancellationToken = default);

    Task<PendingUpdateVerificationBatch?> LoadAsync(CancellationToken cancellationToken = default);

    Task ClearAsync(CancellationToken cancellationToken = default);
}
