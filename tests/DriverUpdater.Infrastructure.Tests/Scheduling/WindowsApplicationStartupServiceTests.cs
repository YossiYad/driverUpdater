using DriverUpdater.Infrastructure.Scheduling;
using FluentAssertions;

namespace DriverUpdater.Infrastructure.Tests.Scheduling;

public class WindowsApplicationStartupServiceTests
{
    [Fact]
    public void BuildArguments_uses_background_argument_when_requested()
    {
        WindowsApplicationStartupService.BuildArguments(startMinimized: true)
            .Should().Be(WindowsApplicationStartupService.BackgroundArgument);
    }

    [Fact]
    public void BuildArguments_is_empty_for_normal_window_start()
    {
        WindowsApplicationStartupService.BuildArguments(startMinimized: false)
            .Should().BeEmpty();
    }
}
