using System.Windows;
using DriverUpdater.App.ViewModels;
using DriverUpdater.App.Views;
using DriverUpdater.Core.Models;
using Microsoft.Extensions.Logging;

namespace DriverUpdater.App.Services;

public sealed class AiResultWindowOpener : IAiResultWindowOpener
{
    private readonly ILogger<AiResultWindowOpener> _logger;

    public AiResultWindowOpener(ILogger<AiResultWindowOpener> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public void Open(DriverInfo driver, UpdateCandidate? candidate, AiVerdict verdict)
    {
        try
        {
            var window = new AiResultWindow(new AiResultViewModel(driver, candidate, verdict))
            {
                Owner = Application.Current?.MainWindow
            };
            window.Show();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not open AI result window for {Device}", driver.DeviceName);
            MessageBox.Show(
                $"Could not open the AI response window.{Environment.NewLine}{Environment.NewLine}{ex.Message}",
                "DriverUpdater",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}
