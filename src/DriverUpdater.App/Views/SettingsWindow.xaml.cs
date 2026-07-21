using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using DriverUpdater.App.ViewModels;
using FluentWindow = Wpf.Ui.Controls.FluentWindow;

namespace DriverUpdater.App.Views;

public partial class SettingsWindow : FluentWindow
{
    private static readonly Uri GeminiApiKeysUri = new("https://aistudio.google.com/api-keys");
    private const int AiTabIndex = 5;
    private const int AboutTabIndex = 6;
    private readonly SettingsViewModel _viewModel;
    private bool _syncingKey;
    private WelcomeWindow? _welcomeWindow;

    public SettingsWindow(SettingsViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    public void SelectAiTab() => SettingsTabs.SelectedIndex = AiTabIndex;

    public void SelectAboutTab() => SettingsTabs.SelectedIndex = AboutTabIndex;

    private void OnOpenWelcomeGuide(object sender, RoutedEventArgs e)
    {
        if (_welcomeWindow is { IsVisible: true })
        {
            _welcomeWindow.Activate();
            return;
        }

        var welcomeWindow = new WelcomeWindow(
            _viewModel.SelectedLanguage,
            _viewModel.ShowGuideOnStartup)
        {
            Owner = this
        };
        welcomeWindow.OpenAiSettingsRequested += (_, _) =>
        {
            SelectAiTab();
            Activate();
        };
        welcomeWindow.OpenAutomaticUpdateSettingsRequested += (_, _) =>
        {
            SelectAboutTab();
            Activate();
        };
        welcomeWindow.Closed += (_, _) =>
        {
            _viewModel.ShowGuideOnStartup = welcomeWindow.ShowOnStartup;
            _welcomeWindow = null;
        };
        _welcomeWindow = welcomeWindow;
        welcomeWindow.Show();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await _viewModel.LoadAsync().ConfigureAwait(true);
    }

    private void OnGeminiKeyBoxLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not PasswordBox passwordBox
            || passwordBox.DataContext is not GeminiApiKeyEntryViewModel entry)
        {
            return;
        }

        _syncingKey = true;
        passwordBox.Password = entry.Value;
        _syncingKey = false;
    }

    private void OnGeminiKeyChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingKey
            || sender is not PasswordBox passwordBox
            || passwordBox.DataContext is not GeminiApiKeyEntryViewModel entry)
        {
            return;
        }

        entry.Value = passwordBox.Password;
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

    private void OnOpenGeminiApiKeys(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(GeminiApiKeysUri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // Opening the browser is best-effort.
        }
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
