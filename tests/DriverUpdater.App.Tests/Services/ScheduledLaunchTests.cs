using DriverUpdater.App.Services;
using FluentAssertions;

namespace DriverUpdater.App.Tests.Services;

public class ScheduledLaunchTests
{
    [Fact]
    public void Parse_returns_none_for_no_args()
    {
        ScheduledLaunch.Parse(Array.Empty<string>()).Should().Be(ScheduledLaunchMode.None);
    }

    [Fact]
    public void Parse_returns_none_for_null()
    {
        ScheduledLaunch.Parse(null).Should().Be(ScheduledLaunchMode.None);
    }

    [Fact]
    public void Parse_returns_none_when_only_the_executable_path_is_present()
    {
        ScheduledLaunch.Parse(new[] { @"C:\Program Files\DriverUpdater\DriverUpdater.exe" })
            .Should().Be(ScheduledLaunchMode.None);
    }

    [Fact]
    public void Parse_maps_scan_only_argument()
    {
        ScheduledLaunch.Parse(new[] { @"C:\app.exe", "--scheduled-scan" })
            .Should().Be(ScheduledLaunchMode.ScanOnly);
    }

    [Fact]
    public void Parse_maps_scan_and_update_argument()
    {
        ScheduledLaunch.Parse(new[] { @"C:\app.exe", "--scheduled-update" })
            .Should().Be(ScheduledLaunchMode.ScanAndUpdate);
    }

    [Fact]
    public void Parse_is_case_insensitive()
    {
        ScheduledLaunch.Parse(new[] { "--SCHEDULED-SCAN" }).Should().Be(ScheduledLaunchMode.ScanOnly);
    }

    [Fact]
    public void Parse_prefers_scan_and_update_when_both_flags_are_present()
    {
        ScheduledLaunch.Parse(new[] { "--scheduled-scan", "--scheduled-update" })
            .Should().Be(ScheduledLaunchMode.ScanAndUpdate);
    }
}
