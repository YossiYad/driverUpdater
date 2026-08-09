using System.Windows;
using DriverUpdater.App.ViewModels;
using DriverUpdater.App.Views;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace DriverUpdater.App.Services;

public sealed class DriverVersionHistoryWindowOpener : IDriverVersionHistoryWindowOpener
{
    private readonly IServiceProvider _services;

    public DriverVersionHistoryWindowOpener(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
    }

    public void Open(DriverInfo driver, Func<DriverVersionRecord, Task<bool>> downgradeAsync, object? owner = null)
    {
        ArgumentNullException.ThrowIfNull(driver);
        ArgumentNullException.ThrowIfNull(downgradeAsync);

        // The view model needs per-call state (the driver and the callback), so it is built by
        // hand from DI-resolved dependencies instead of being registered itself.
        var viewModel = new DriverVersionHistoryViewModel(
            driver,
            _services.GetRequiredService<IDriverVersionHistoryStore>(),
            _services.GetRequiredService<IDriverStoreBrowser>(),
            downgradeAsync);
        var window = new DriverVersionHistoryWindow(viewModel)
        {
            Owner = owner as Window
                ?? Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
        };
        window.ShowDialog();
    }
}
