using DriverUpdater.Core.Models;
using DriverUpdater.Core.Options;

namespace DriverUpdater.App.Services;

public sealed class ApplicationBehaviorState
{
    private readonly object _sync = new();
    private ApplicationSettings _current = new();

    public WindowCloseBehavior CloseBehavior
    {
        get
        {
            lock (_sync)
            {
                return _current.CloseBehavior;
            }
        }
    }

    public bool ShouldStartHidden
    {
        get
        {
            lock (_sync)
            {
                return _current.StartWithWindows
                    && _current.StartMinimized
                    && _current.CloseBehavior == WindowCloseBehavior.KeepRunningInBackground;
            }
        }
    }

    public void Apply(ApplicationSettings? settings)
    {
        settings ??= new ApplicationSettings();
        lock (_sync)
        {
            _current = new ApplicationSettings
            {
                CloseBehavior = settings.CloseBehavior == WindowCloseBehavior.KeepRunningInBackground
                    ? WindowCloseBehavior.KeepRunningInBackground
                    : WindowCloseBehavior.ExitApplication,
                StartWithWindows = settings.StartWithWindows,
                StartMinimized = settings.StartWithWindows
                    && settings.CloseBehavior == WindowCloseBehavior.KeepRunningInBackground
                    && settings.StartMinimized
            };
        }
    }
}
