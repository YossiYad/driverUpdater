using System.Windows;
using DriverUpdater.App.ViewModels;
using DriverUpdater.App.Views;
using Microsoft.Extensions.DependencyInjection;

namespace DriverUpdater.App.Services;

public sealed class ExcludedDriverSelectionWindowOpener : IExcludedDriverSelectionWindowOpener
{
    private readonly IServiceProvider _services;

    public ExcludedDriverSelectionWindowOpener(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
    }

    public void Open(object? owner = null)
    {
        var window = _services.GetRequiredService<ExcludedDriverSelectionWindow>();
        window.Owner = owner as Window ?? Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
        window.ShowDialog();
    }
}
