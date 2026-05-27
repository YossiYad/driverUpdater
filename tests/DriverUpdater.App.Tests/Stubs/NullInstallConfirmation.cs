using DriverUpdater.App.ViewModels;
using DriverUpdater.Core.Models;

namespace DriverUpdater.App.Tests.Stubs;

public sealed class NullInstallConfirmation : IInstallConfirmation
{
    public InstallOptions? Confirm(UpdateOperation operation, bool dryRun) => null;
}
