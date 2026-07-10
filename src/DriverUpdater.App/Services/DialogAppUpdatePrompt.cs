using System.Globalization;
using System.Windows;

namespace DriverUpdater.App.Services;

public sealed class DialogAppUpdatePrompt : IAppUpdatePrompt
{
    private readonly ILocalizationService _localization;

    public DialogAppUpdatePrompt(ILocalizationService localization)
    {
        ArgumentNullException.ThrowIfNull(localization);
        _localization = localization;
    }

    public bool Confirm(string? version)
    {
        var title = TryFindString("Update.AvailableTitle") ?? "Update available";
        var template = TryFindString("Update.AvailableMessage")
            ?? "A new version ({0}) is available. Update now? The app will restart.";
        var message = string.Format(CultureInfo.CurrentCulture, template, version ?? string.Empty);

        var options = _localization.IsRightToLeft
            ? MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign
            : MessageBoxOptions.None;

        var result = MessageBox.Show(
            Application.Current?.MainWindow!,
            message,
            title,
            MessageBoxButton.YesNo,
            MessageBoxImage.Information,
            MessageBoxResult.Yes,
            options);

        return result == MessageBoxResult.Yes;
    }

    private static string? TryFindString(string key) =>
        Application.Current?.TryFindResource(key) as string;
}
