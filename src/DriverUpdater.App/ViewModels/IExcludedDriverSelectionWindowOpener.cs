namespace DriverUpdater.App.ViewModels;

/// <summary>
/// Opens the list of drivers the app must never update. Modal, so the Sources tab can refresh
/// the count as soon as it returns.
/// </summary>
public interface IExcludedDriverSelectionWindowOpener
{
    void Open(object? owner = null);
}
