using DriverUpdater.App.ViewModels;

namespace DriverUpdater.App.Tests.Stubs;

public sealed class NullHistoryWindowOpener : IHistoryWindowOpener
{
    public void Open()
    {
    }
}
