using DriverUpdater.Core.Models;

namespace DriverUpdater.Core.Abstractions;

public interface IVendorPageInstallerResolver
{
    Task<UpdateCandidate?> TryResolveAsync(UpdateCandidate candidate, CancellationToken cancellationToken = default);
}
