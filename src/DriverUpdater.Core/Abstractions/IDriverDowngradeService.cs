using DriverUpdater.Core.Models;
using DriverUpdater.Core.Results;

namespace DriverUpdater.Core.Abstractions;

/// <summary>
/// Rolls a device back to an older driver version whose package is still in the Windows
/// DriverStore. The currently bound (newer) package is exported to the backup folder first,
/// then removed so Windows re-binds the best remaining package - the older one.
/// </summary>
public interface IDriverDowngradeService
{
    Task<Result<DriverDowngradeOutcome>> DowngradeAsync(
        DriverInfo driver,
        DriverVersionRecord target,
        CancellationToken cancellationToken = default);
}
