using System.Windows;
using DriverUpdater.App.ViewModels;
using DriverUpdater.App.Views;
using Microsoft.Extensions.DependencyInjection;

namespace DriverUpdater.App.Services;

public sealed class LogsWindowOpener : ILogsWindowOpener
{
    private readonly IServiceProvider _services;
    private LogsWindow? _current;

    public LogsWindowOpener(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
    }

    public void Open()
    {
        if (_current is { IsLoaded: true })
        {
            if (_current.WindowState == WindowState.Minimized)
            {
                _current.WindowState = WindowState.Normal;
            }
            _current.Activate();
            return;
        }

        _current = _services.GetRequiredService<LogsWindow>();
        _current.Owner = Application.Current?.MainWindow;
        _current.Closed += (_, _) => _current = null;
        _current.Show();
    }
}
