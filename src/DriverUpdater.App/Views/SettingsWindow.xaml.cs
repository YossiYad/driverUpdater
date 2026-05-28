using System.Windows;
using DriverUpdater.App.ViewModels;

namespace DriverUpdater.App.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;

    public SettingsWindow(SettingsViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await _viewModel.LoadAsync().ConfigureAwait(true);
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
