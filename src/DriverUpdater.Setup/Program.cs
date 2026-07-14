using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace DriverUpdater.Setup;

internal static partial class Program
{
    private const string PayloadResourceName = "DriverUpdater.Setup.Payload.exe";
    private const string AppProcessName = "DriverUpdater";
    private const uint MessageBoxError = 0x00000010;

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (!IsHelpRequest(args))
            {
                StopRunningAppInstances();
            }

            return RunEmbeddedSetup(args);
        }
        catch (Exception ex)
        {
            ShowError($"DriverUpdater setup could not continue.\n\n{ex.Message}");
            return 1;
        }
    }

    private static bool IsHelpRequest(IEnumerable<string> args)
    {
        return args.Any(argument => argument is "--help" or "-h");
    }

    private static void StopRunningAppInstances()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var processes = Process.GetProcessesByName(AppProcessName);
            if (processes.Length == 0)
            {
                return;
            }

            foreach (var process in processes)
            {
                using (process)
                {
                    StopProcess(process);
                }
            }
        }

        if (HasRunningAppInstances())
        {
            throw new InvalidOperationException("A running DriverUpdater process could not be closed.");
        }
    }

    private static void StopProcess(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                return;
            }

            if (process.CloseMainWindow() && process.WaitForExit(3_000))
            {
                return;
            }
        }
        catch (InvalidOperationException)
        {
            return;
        }

        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            return;
        }

        if (!process.WaitForExit(10_000))
        {
            throw new InvalidOperationException($"DriverUpdater process {process.Id} did not exit.");
        }
    }

    private static bool HasRunningAppInstances()
    {
        var processes = Process.GetProcessesByName(AppProcessName);
        try
        {
            return processes.Length > 0;
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    private static int RunEmbeddedSetup(IReadOnlyCollection<string> args)
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"DriverUpdater-Setup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var setupPath = Path.Combine(tempDirectory, "DriverUpdater-Velopack-Setup.exe");
            ExtractSetup(setupPath);

            var startInfo = new ProcessStartInfo
            {
                FileName = setupPath,
                WorkingDirectory = tempDirectory,
                UseShellExecute = false
            };

            foreach (var argument in args)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var setup = Process.Start(startInfo)
                ?? throw new InvalidOperationException("The embedded Velopack setup could not be started.");
            setup.WaitForExit();
            return setup.ExitCode;
        }
        finally
        {
            TryDeleteDirectory(tempDirectory);
        }
    }

    private static void ExtractSetup(string destinationPath)
    {
        using var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream(PayloadResourceName)
            ?? throw new InvalidOperationException("The embedded Velopack setup is missing.");
        using var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        payload.CopyTo(destination);
    }

    private static void TryDeleteDirectory(string directory)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
                return;
            }
            catch (IOException)
            {
                if (attempt == 2)
                {
                    return;
                }

                Thread.Sleep(200);
            }
            catch (UnauthorizedAccessException)
            {
                if (attempt == 2)
                {
                    return;
                }

                Thread.Sleep(200);
            }
        }
    }

    private static void ShowError(string message)
    {
        MessageBox(nint.Zero, message, "DriverUpdater Setup", MessageBoxError);
    }

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBox(nint windowHandle, string text, string caption, uint type);
}
