namespace DriverUpdater.App.Services;

public interface IAppUpdatePrompt
{
    /// <summary>
    /// Asks the user whether to install the available app update now. Returns true when the
    /// user accepts. Implementations show a modal dialog on the UI thread.
    /// </summary>
    bool Confirm(string? version);
}
