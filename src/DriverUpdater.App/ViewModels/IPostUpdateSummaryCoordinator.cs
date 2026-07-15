using DriverUpdater.Core.Models;

namespace DriverUpdater.App.ViewModels;

public interface IPostUpdateSummaryCoordinator
{
    Task<UpdateVerificationReport?> CompleteRunAsync(
        IReadOnlyCollection<UpdateOperation> operations,
        Action<UpdateVerificationReport>? beforeSummaryOpen = null,
        CancellationToken cancellationToken = default);

    Task ResumeAfterRestartAsync(CancellationToken cancellationToken = default);
}
