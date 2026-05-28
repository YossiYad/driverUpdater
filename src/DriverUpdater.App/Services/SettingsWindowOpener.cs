using System.Windows;
using DriverUpdater.App.ViewModels;
using DriverUpdater.App.Views;
using Microsoft.Extensions.DependencyInjection;

namespace DriverUpdater.App.Services;

public sealed class SettingsWindowOpener : ISettingsWindowOpener
{
    private readonly IServiceProvider _services;

    public SettingsWindowOpener(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
    }

    public void Open()
    {
        var window = _services.GetRequiredService<SettingsWindow>();
        window.Owner = Application.Current?.MainWindow;
        window.ShowDialog();
    }
}
