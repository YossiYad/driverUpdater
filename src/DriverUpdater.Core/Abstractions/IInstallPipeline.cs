using DriverUpdater.Core.Models;

namespace DriverUpdater.Core.Abstractions;

public interface IInstallPipeline
{
    Task<UpdateOperation> ExecuteAsync(
        UpdateOperation operation,
        InstallOptions options,
        IProgress<UpdateOperation>? progress = null,
        CancellationToken cancellationToken = default);
}
