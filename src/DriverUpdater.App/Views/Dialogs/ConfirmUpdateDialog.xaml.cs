using System.Windows;
using DriverUpdater.App.ViewModels;
using DriverUpdater.Core.Models;

namespace DriverUpdater.App.Views.Dialogs;

public partial class ConfirmUpdateDialog : Window
{
    public ConfirmUpdateDialogViewModel ViewModel { get; }

    public ConfirmUpdateDialog(UpdateOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        InitializeComponent();
        ViewModel = new ConfirmUpdateDialogViewModel(operation);
        DataContext = ViewModel;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnProceed(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
