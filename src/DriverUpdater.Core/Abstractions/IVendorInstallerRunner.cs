using DriverUpdater.Core.Models;

namespace DriverUpdater.Core.Abstractions;

public interface IVendorInstallerRunner
{
    Task<ProcessResult> RunAsync(string fileName, string arguments, CancellationToken cancellationToken = default);
}
