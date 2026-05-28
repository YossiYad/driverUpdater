using DriverUpdater.App.ViewModels;

namespace DriverUpdater.App.Tests.Stubs;

public sealed class NullSettingsWindowOpener : ISettingsWindowOpener
{
    public void Open()
    {
    }
}
