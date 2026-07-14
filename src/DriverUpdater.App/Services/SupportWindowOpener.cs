using System.Windows;
using DriverUpdater.App.ViewModels;
using DriverUpdater.App.Views;

namespace DriverUpdater.App.Services;

public sealed class SupportWindowOpener : ISupportWindowOpener
{
    private readonly IExternalLinkOpener _externalLinkOpener;

    public SupportWindowOpener(IExternalLinkOpener externalLinkOpener)
    {
        ArgumentNullException.ThrowIfNull(externalLinkOpener);
        _externalLinkOpener = externalLinkOpener;
    }

    public void Open()
    {
        var window = new SupportWindow(new SupportViewModel(_externalLinkOpener))
        {
            Owner = Application.Current?.MainWindow
        };
        window.Show();
    }
}
