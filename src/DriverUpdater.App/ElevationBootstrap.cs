using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace DriverUpdater.App;

internal static class ElevationBootstrap
{
    internal const int ElevationCanceledErrorCode = 1223;

    public static bool EnsureElevated()
    {
        if (!OperatingSystem.IsWindows() || IsRunningAsAdministrator())
        {
            return true;
        }

        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new InvalidOperationException("Could not determine the application executable path.");
        }

        try
        {
            Process.Start(CreateStartInfo(executablePath, Environment.GetCommandLineArgs().Skip(1)));
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ElevationCanceledErrorCode)
        {
            return false;
        }

        return false;
    }

    internal static ProcessStartInfo CreateStartInfo(string executablePath, IEnumerable<string> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(arguments);

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = AppContext.BaseDirectory
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static bool IsRunningAsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
