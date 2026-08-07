using DriverUpdater.Core.Models;

namespace DriverUpdater.App.ViewModels;

public interface IDriverVersionHistoryWindowOpener
{
    /// <summary>
    /// Opens the version history dialog for one device. The callback performs the actual
    /// downgrade and returns true when it verified the older version is now bound.
    /// </summary>
    void Open(DriverInfo driver, Func<DriverVersionRecord, Task<bool>> downgradeAsync, object? owner = null);
}
