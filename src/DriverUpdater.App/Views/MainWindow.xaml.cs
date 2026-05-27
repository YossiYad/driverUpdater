using System.Security.Principal;
using System.Windows;

namespace DriverUpdater.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        if (!IsRunningAsAdministrator())
        {
            AdminBadge.Background = System.Windows.Media.Brushes.DarkOrange;
            AdminBadgeText.Text = "Not elevated";
            StatusText.Text = "Warning: not running as administrator. Most operations will fail.";
        }
    }

    private static bool IsRunningAsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }
}
