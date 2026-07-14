using System;
using Velopack;

namespace DriverUpdater.App;

public static class Program
{
    [STAThread]
    public static void Main()
    {
        VelopackApp.Build().Run();

        // Velopack's per-user installer launches hooks and the app without elevation.
        // Requiring administrator in the executable manifest makes those launches fail
        // with Win32 error 740. Hooks exit from Run() above; only a normal app launch
        // reaches this point and asks Windows for the privileges driver operations need.
        if (!ElevationBootstrap.EnsureElevated())
        {
            return;
        }

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
