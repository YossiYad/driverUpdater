using DriverUpdater.Core.Models;

namespace DriverUpdater.Core.Abstractions;

public interface ICatalogHttpClient
{
    Task<IReadOnlyList<CatalogSearchHit>> SearchAsync(string query, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CatalogDownloadInfo>> GetDownloadsAsync(
        IReadOnlyCollection<string> updateIds,
        CancellationToken cancellationToken = default);
}
