using DriverUpdater.Core.Models;

namespace DriverUpdater.Core.Abstractions;

public interface IDriverCacheStore
{
    Task<DriverCacheSnapshot?> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(DriverCacheSnapshot snapshot, CancellationToken cancellationToken = default);
}
