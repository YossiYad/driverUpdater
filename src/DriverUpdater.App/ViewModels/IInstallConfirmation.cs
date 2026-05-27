using DriverUpdater.Core.Models;

namespace DriverUpdater.App.ViewModels;

public interface IInstallConfirmation
{
    InstallOptions? Confirm(UpdateOperation operation, bool dryRun);
}
