using System.IO;
using System.Windows;
using Velopack;

namespace DriverUpdater.App;

internal static class Program
{
    [STAThread]
    public static void Main()
    {
        try
        {
            VelopackApp.Build().Run();

            var app = new App();
            app.InitializeComponent();
            app.Run();
        }
        catch (Exception ex)
        {
            ReportFatalStartupFailure(ex);
        }
    }

    private static void ReportFatalStartupFailure(Exception exception)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DriverUpdater");
            Directory.CreateDirectory(directory);
            File.AppendAllText(
                Path.Combine(directory, "startup-failures.log"),
                $"[{DateTimeOffset.UtcNow:O}] {exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // The fallback reporter must never hide the original startup failure.
        }

        MessageBox.Show(
            "DriverUpdater could not start. A diagnostic log was written under your Local AppData folder.",
            "DriverUpdater",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }
}
