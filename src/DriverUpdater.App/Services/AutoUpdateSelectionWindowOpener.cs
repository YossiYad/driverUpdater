using System.Windows;
using DriverUpdater.App.ViewModels;
using DriverUpdater.App.Views;
using Microsoft.Extensions.DependencyInjection;

namespace DriverUpdater.App.Services;

public sealed class AutoUpdateSelectionWindowOpener : IAutoUpdateSelectionWindowOpener
{
    private readonly IServiceProvider _services;

    public AutoUpdateSelectionWindowOpener(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
    }

    public void Open(object? owner = null)
    {
        var window = _services.GetRequiredService<AutoUpdateSelectionWindow>();
        window.Owner = owner as Window ?? Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
        window.ShowDialog();
    }
}
