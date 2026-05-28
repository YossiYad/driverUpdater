using System.Windows;
using DriverUpdater.App.ViewModels;
using DriverUpdater.App.Views;
using Microsoft.Extensions.DependencyInjection;

namespace DriverUpdater.App.Services;

public sealed class HistoryWindowOpener : IHistoryWindowOpener
{
    private readonly IServiceProvider _services;

    public HistoryWindowOpener(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
    }

    public void Open()
    {
        var window = _services.GetRequiredService<HistoryWindow>();
        window.Owner = Application.Current?.MainWindow;
        window.Show();
    }
}
