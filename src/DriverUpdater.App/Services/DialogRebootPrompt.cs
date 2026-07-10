using System.Diagnostics;
using System.Globalization;
using System.Windows;

namespace DriverUpdater.App.Services;

public sealed class DialogRebootPrompt : IRebootPrompt
{
    // Give the user a short grace period after confirming so the app can close cleanly and
    // any last-second "save your work" prompts from other apps can appear.
    private const int RestartDelaySeconds = 20;

    private readonly ILocalizationService _localization;

    public DialogRebootPrompt(ILocalizationService localization)
    {
        ArgumentNullException.ThrowIfNull(localization);
        _localization = localization;
    }

    public bool ConfirmRestartNow(int rebootRequiredDriverCount)
    {
        var title = TryFindString("Reboot.RequiredTitle") ?? "Restart required";
        var template = TryFindString("Reboot.RequiredMessage")
            ?? "{0} driver update(s) need a restart to finish. Restart the computer now?";
        var message = string.Format(CultureInfo.CurrentCulture, template, rebootRequiredDriverCount);

        var options = _localization.IsRightToLeft
            ? MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign
            : MessageBoxOptions.None;

        var result = MessageBox.Show(
            Application.Current?.MainWindow!,
            message,
            title,
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No,
            options);

        return result == MessageBoxResult.Yes;
    }

    public void RestartNow()
    {
        var comment = TryFindString("Reboot.ShutdownComment")
            ?? "DriverUpdater is restarting to finish driver installation.";

        // shutdown.exe /r = restart, /t = delay in seconds, /c = comment shown to the user.
        var psi = new ProcessStartInfo("shutdown.exe")
        {
            Arguments = $"/r /t {RestartDelaySeconds} /c \"{comment}\"",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        Process.Start(psi);
    }

    private static string? TryFindString(string key) =>
        Application.Current?.TryFindResource(key) as string;
}
