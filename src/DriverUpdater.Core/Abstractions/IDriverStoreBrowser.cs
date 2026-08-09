using DriverUpdater.Core.Models;

namespace DriverUpdater.Core.Abstractions;

/// <summary>
/// Enumerates the third-party driver packages currently present in the Windows DriverStore.
/// Used to decide whether a recorded older version can still be reinstalled without any backup.
/// </summary>
public interface IDriverStoreBrowser
{
    Task<IReadOnlyList<DriverStorePackage>> EnumeratePackagesAsync(CancellationToken cancellationToken = default);
}
