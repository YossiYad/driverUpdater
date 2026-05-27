using DriverUpdater.Core.Models;

namespace DriverUpdater.Core.Abstractions;

public interface IWuApiClient
{
    IAsyncEnumerable<WuDriverUpdateRecord> SearchDriverUpdatesAsync(CancellationToken cancellationToken = default);
}
