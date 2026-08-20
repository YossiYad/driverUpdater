namespace DriverUpdater.App.ViewModels;

/// <summary>
/// Shows what the AI decided before "Update with AI" installs anything, and hands back the rows
/// the user approved. A null result means the run was cancelled and nothing may be installed.
/// </summary>
public interface IAiUpdatePlanConfirmation
{
    Task<IReadOnlyList<DriverRowViewModel>?> ConfirmAsync(
        AiUpdatePlan plan,
        CancellationToken cancellationToken = default);
}
