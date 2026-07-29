using DriverUpdater.Core.Models;

namespace DriverUpdater.Core.Abstractions;

public interface IVendorPageInstallerResolver
{
    Task<VendorPageResolution> TryResolveAsync(UpdateCandidate candidate, CancellationToken cancellationToken = default);
}
