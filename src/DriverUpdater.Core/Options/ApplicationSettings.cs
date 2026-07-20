using DriverUpdater.Core.Models;

namespace DriverUpdater.Core.Options;

public sealed class ApplicationSettings
{
    public WindowCloseBehavior CloseBehavior { get; set; } = WindowCloseBehavior.ExitApplication;
    public bool StartWithWindows { get; set; }
    public bool StartMinimized { get; set; }
}
