namespace DriverUpdater.Infrastructure;

internal static class WindowsSystemPathResolver
{
    internal static string GetNativeSystemDirectory()
    {
        var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return ResolveNativeSystemDirectory(
            windowsDirectory,
            Environment.Is64BitOperatingSystem,
            Environment.Is64BitProcess);
    }

    internal static string ResolveNativeSystemDirectory(
        string windowsDirectory,
        bool is64BitOperatingSystem,
        bool is64BitProcess)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(windowsDirectory);

        // A 32-bit process on 64-bit Windows is transparently redirected from
        // System32 to SysWOW64. Sysnative is the documented virtual path that lets
        // the process invoke the native pnputil and Windows PowerShell binaries.
        var directoryName = is64BitOperatingSystem && !is64BitProcess
            ? "Sysnative"
            : "System32";
        return Path.Combine(windowsDirectory, directoryName);
    }
}
