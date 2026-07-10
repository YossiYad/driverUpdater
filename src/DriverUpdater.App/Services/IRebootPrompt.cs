namespace DriverUpdater.App.Services;

public interface IRebootPrompt
{
    /// <summary>
    /// Asks the user whether to restart the computer now to finish driver installations that
    /// reported "reboot required". Returns true when the user accepts. Implementations show a
    /// modal dialog on the UI thread. Only called when at least one update needs a reboot.
    /// </summary>
    bool ConfirmRestartNow(int rebootRequiredDriverCount);

    /// <summary>
    /// Initiates a system restart. Called only after <see cref="ConfirmRestartNow"/> returned true.
    /// </summary>
    void RestartNow();
}
