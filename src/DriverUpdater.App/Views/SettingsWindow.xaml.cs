using System.Windows;
using System.Windows.Controls;
using DriverUpdater.App.ViewModels;

namespace DriverUpdater.App.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;
    private bool _syncingKey;

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

        // PasswordBox.Password is not bindable, so sync it after the view model has
        // loaded its settings and again whenever the user types.
        _syncingKey = true;
        GeminiKeyBox.Password = _viewModel.GeminiApiKey;
        _syncingKey = false;
    }

    private void OnGeminiKeyChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingKey)
        {
            return;
        }
        _viewModel.GeminiApiKey = GeminiKeyBox.Password;
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
