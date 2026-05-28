using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;

namespace DriverUpdater.App.Tests.Stubs;

public sealed class NullInstallPipeline : IInstallPipeline
{
    public Task<UpdateOperation> ExecuteAsync(
        UpdateOperation operation,
        InstallOptions options,
        IProgress<UpdateOperation>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(operation);
}
