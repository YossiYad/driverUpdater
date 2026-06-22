using System.Windows;
using DriverUpdater.App.ViewModels;
using DriverUpdater.App.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DriverUpdater.App.Services;

public sealed class LogsWindowOpener : ILogsWindowOpener
{
    private readonly IServiceProvider _services;
    private readonly ILogger<LogsWindowOpener> _logger;
    private LogsWindow? _current;

    public LogsWindowOpener(IServiceProvider services, ILogger<LogsWindowOpener> logger)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(logger);
        _services = services;
        _logger = logger;
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

        try
        {
            _current = _services.GetRequiredService<LogsWindow>();
            _current.Owner = Application.Current?.MainWindow;
            _current.Closed += (_, _) => _current = null;
            _current.Show();
        }
        catch (Exception ex)
        {
            _current = null;
            _logger.LogError(ex, "Could not open the logs window");
            MessageBox.Show(
                $"Could not open the logs window.{Environment.NewLine}{Environment.NewLine}{ex.Message}",
                "DriverUpdater",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}
