using System.Windows;
using DriverUpdater.App.ViewModels;
using DriverUpdater.App.Views.Dialogs;
using DriverUpdater.Core.Models;

namespace DriverUpdater.App.Services;

public sealed class DialogInstallConfirmation : IInstallConfirmation
{
    public InstallOptions? Confirm(UpdateOperation operation, bool dryRun)
    {
        var dialog = new ConfirmUpdateDialog(operation)
        {
            Owner = Application.Current?.MainWindow
        };
        var result = dialog.ShowDialog();
        if (result != true)
        {
            return null;
        }
        return dialog.ViewModel.BuildOptions(dryRun);
    }
}
