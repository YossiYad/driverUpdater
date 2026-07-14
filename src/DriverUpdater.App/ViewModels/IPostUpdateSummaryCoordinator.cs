using DriverUpdater.Core.Models;

namespace DriverUpdater.App.ViewModels;

public interface IPostUpdateSummaryCoordinator
{
    Task CompleteRunAsync(
        IReadOnlyCollection<UpdateOperation> operations,
        CancellationToken cancellationToken = default);

    Task ResumeAfterRestartAsync(CancellationToken cancellationToken = default);
}
