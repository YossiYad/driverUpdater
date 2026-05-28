using System.Security.Principal;
using System.Windows;
using DriverUpdater.App.ViewModels;

namespace DriverUpdater.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        _viewModel.ScrollToRowRequested += OnScrollToRowRequested;

        if (!IsRunningAsAdministrator())
        {
            AdminBadge.Background = System.Windows.Media.Brushes.DarkOrange;
            AdminBadgeText.SetResourceReference(System.Windows.Controls.TextBlock.TextProperty, "App.NotElevated");
            viewModel.StatusText = "Warning: not running as administrator. Most operations will fail.";
        }
    }

    private void OnScrollToRowRequested(object? sender, DriverRowViewModel row)
    {
        // The pipeline raises this each time it moves on to the next install target. We
        // jump the grid to that row so the user always sees the active update without
        // having to scroll through the 250+ rows of unaffected drivers.
        if (DriversGrid.Items.Contains(row))
        {
            DriversGrid.ScrollIntoView(row);
            DriversGrid.SelectedItem = row;
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await _viewModel.InitializeAsync().ConfigureAwait(true);
    }

    private static bool IsRunningAsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }
}
