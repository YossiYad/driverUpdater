using DriverUpdater.Infrastructure.Scheduling;
using FluentAssertions;

namespace DriverUpdater.Infrastructure.Tests.Scheduling;

public class WindowsPostRebootStartupServiceTests
{
    [Fact]
    public void BuildCommand_quotes_path_and_adds_verification_argument()
    {
        var command = WindowsPostRebootStartupService.BuildCommand(
            @"C:\Program Files\DriverUpdater\DriverUpdater.exe");

        command.Should().Be(
            "\"C:\\Program Files\\DriverUpdater\\DriverUpdater.exe\" --verify-after-reboot");
    }
}
