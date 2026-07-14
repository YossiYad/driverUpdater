using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using DriverUpdater.App.ViewModels;
using FluentWindow = Wpf.Ui.Controls.FluentWindow;

namespace DriverUpdater.App.Views;

public partial class SettingsWindow : FluentWindow
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

    private void OnHyperlinkRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // Opening the browser is best-effort; the URL is still visible to copy.
        }
        e.Handled = true;
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
