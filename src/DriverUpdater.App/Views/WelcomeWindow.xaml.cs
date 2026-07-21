using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using DriverUpdater.Core.Models;
using FluentWindow = Wpf.Ui.Controls.FluentWindow;

namespace DriverUpdater.App.Views;

public partial class WelcomeWindow : FluentWindow
{
    private bool _syncingStartupPreference;

    public event EventHandler? OpenAiSettingsRequested;
    public event EventHandler? OpenAutomaticUpdateSettingsRequested;

    public WelcomeWindow(AppLanguage language, bool showOnStartup = true)
    {
        InitializeComponent();
        _syncingStartupPreference = true;
        EnglishDontShowAgain.IsChecked = !showOnStartup;
        HebrewDontShowAgain.IsChecked = !showOnStartup;
        _syncingStartupPreference = false;
        LanguageSelector.SelectedIndex = ShouldUseHebrew(language) ? 1 : 0;
    }

    public bool ShowOnStartup => EnglishDontShowAgain.IsChecked != true;

    private static bool ShouldUseHebrew(AppLanguage language) =>
        language == AppLanguage.Hebrew
        || (language == AppLanguage.SystemDefault
            && string.Equals(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName, "he", StringComparison.OrdinalIgnoreCase));

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EnglishPanel is null || HebrewPanel is null)
        {
            return;
        }

        var useHebrew = LanguageSelector.SelectedIndex == 1;
        EnglishPanel.Visibility = useHebrew ? Visibility.Collapsed : Visibility.Visible;
        HebrewPanel.Visibility = useHebrew ? Visibility.Visible : Visibility.Collapsed;
        Title = useHebrew ? "ברוכים הבאים ל־DriverUpdater" : "Welcome to DriverUpdater";
        WelcomeTitleBar.Title = Title;
    }

    private void OnOpenAiSettings(object sender, RoutedEventArgs e)
    {
        OpenAiSettingsRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnOpenAutomaticUpdateSettings(object sender, RoutedEventArgs e)
    {
        OpenAutomaticUpdateSettingsRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnDontShowAgainChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingStartupPreference || sender is not CheckBox source)
        {
            return;
        }

        _syncingStartupPreference = true;
        EnglishDontShowAgain.IsChecked = source.IsChecked;
        HebrewDontShowAgain.IsChecked = source.IsChecked;
        _syncingStartupPreference = false;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnLinkRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // The address remains visible and can still be copied if the browser cannot open.
        }
        e.Handled = true;
    }
}
