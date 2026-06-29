using DriverUpdater.Infrastructure.Scheduling;

namespace DriverUpdater.App.Services;

public enum ScheduledLaunchMode
{
    None,
    ScanOnly,
    ScanAndUpdate
}

// Maps the command-line arguments the Windows scheduled task launches the app with
// (see WindowsTaskSchedulerService) onto the headless run mode. When neither flag is
// present the app starts its normal interactive UI.
public static class ScheduledLaunch
{
    public static ScheduledLaunchMode FromCommandLine() =>
        Parse(Environment.GetCommandLineArgs());

    public static ScheduledLaunchMode Parse(IEnumerable<string>? args)
    {
        if (args is null)
        {
            return ScheduledLaunchMode.None;
        }

        var mode = ScheduledLaunchMode.None;
        foreach (var arg in args)
        {
            if (string.Equals(arg, WindowsTaskSchedulerService.ScanAndUpdateArgument, StringComparison.OrdinalIgnoreCase))
            {
                // Update implies scan, and is the stronger intent, so it wins outright.
                return ScheduledLaunchMode.ScanAndUpdate;
            }
            if (string.Equals(arg, WindowsTaskSchedulerService.ScanOnlyArgument, StringComparison.OrdinalIgnoreCase))
            {
                mode = ScheduledLaunchMode.ScanOnly;
            }
        }
        return mode;
    }
}
