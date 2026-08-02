namespace DriverUpdater.App.ViewModels;

/// <summary>
/// Opens the list of drivers the custom schedule may update. Modal, so the Schedule tab can
/// refresh the count as soon as it returns.
/// </summary>
public interface IAutoUpdateSelectionWindowOpener
{
    void Open(object? owner = null);
}
