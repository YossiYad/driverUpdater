using System.Windows;
using DriverUpdater.App.ViewModels;
using DriverUpdater.App.Views;
using DriverUpdater.Core.Models;
using Microsoft.Extensions.Logging;

namespace DriverUpdater.App.Services;

public sealed class UpdateSummaryWindowOpener : IUpdateSummaryWindowOpener
{
    private readonly ILogger<UpdateSummaryWindowOpener> _logger;

    public UpdateSummaryWindowOpener(ILogger<UpdateSummaryWindowOpener> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public void Open(UpdateVerificationReport report, AppLanguage language)
    {
        try
        {
            var window = new UpdateSummaryWindow(new UpdateSummaryViewModel(report, language), language)
            {
                Owner = Application.Current?.MainWindow
            };
            window.ShowDialog();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not open the update verification summary window");
        }
    }
}
