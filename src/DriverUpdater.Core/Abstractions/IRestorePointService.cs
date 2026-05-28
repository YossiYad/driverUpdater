using DriverUpdater.Core.Models;
using DriverUpdater.Core.Results;

namespace DriverUpdater.Core.Abstractions;

public interface IRestorePointService
{
    Task<bool> IsSystemRestoreEnabledAsync(CancellationToken cancellationToken = default);

    Task<Result<RestorePointInfo>> CreateRestorePointAsync(string description, CancellationToken cancellationToken = default);
}
