using DriverUpdater.Core.Models;
using DriverUpdater.Core.Results;

namespace DriverUpdater.Core.Abstractions;

public interface IWuApiClient
{
    IAsyncEnumerable<WuDriverUpdateRecord> SearchDriverUpdatesAsync(CancellationToken cancellationToken = default);

    Task<Result<WuInstallResult>> DownloadAndInstallAsync(
        string updateId,
        CancellationToken cancellationToken = default);
}
