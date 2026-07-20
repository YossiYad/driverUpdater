using DriverUpdater.Core.Models;

namespace DriverUpdater.Core.Abstractions;

public interface IDriverCacheStore
{
    event EventHandler? Cleared;

    Task<DriverCacheSnapshot?> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(DriverCacheSnapshot snapshot, CancellationToken cancellationToken = default);

    Task<int> ClearAsync(CancellationToken cancellationToken = default);
}
