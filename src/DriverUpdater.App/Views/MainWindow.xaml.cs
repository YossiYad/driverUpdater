using System.Security.Principal;
using System.Windows;
using DriverUpdater.App.ViewModels;

namespace DriverUpdater.App.Views;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        DataContext = viewModel;

        if (!IsRunningAsAdministrator())
        {
            AdminBadge.Background = System.Windows.Media.Brushes.DarkOrange;
            AdminBadgeText.Text = "Not elevated";
            viewModel.StatusText = "Warning: not running as administrator. Most operations will fail.";
        }
    }

    private static bool IsRunningAsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }
}
