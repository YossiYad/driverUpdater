using DriverUpdater.App.Logging;
using DriverUpdater.App.ViewModels;
using DriverUpdater.App.Views;
using FluentAssertions;

namespace DriverUpdater.App.Tests.Views;

public class LogsWindowTests
{
    [WpfFact]
    public void Constructor_loads_xaml_without_throwing()
    {
        using var viewModel = new LogsViewModel(new InMemoryLogSink());
        var window = new LogsWindow(viewModel);

        try
        {
            window.Should().NotBeNull();
            window.Title.Should().Contain("Logs");
        }
        finally
        {
            window.Close();
        }
    }
}
