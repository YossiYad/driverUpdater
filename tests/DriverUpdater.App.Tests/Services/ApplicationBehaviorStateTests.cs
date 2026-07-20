using DriverUpdater.App.Services;
using DriverUpdater.Core.Models;
using DriverUpdater.Core.Options;
using FluentAssertions;

namespace DriverUpdater.App.Tests.Services;

public class ApplicationBehaviorStateTests
{
    [Fact]
    public void Default_behavior_exits_the_application()
    {
        var state = new ApplicationBehaviorState();

        state.CloseBehavior.Should().Be(WindowCloseBehavior.ExitApplication);
        state.ShouldStartHidden.Should().BeFalse();
    }

    [Fact]
    public void Background_start_requires_all_related_options()
    {
        var state = new ApplicationBehaviorState();
        state.Apply(new ApplicationSettings
        {
            CloseBehavior = WindowCloseBehavior.KeepRunningInBackground,
            StartWithWindows = true,
            StartMinimized = true
        });

        state.CloseBehavior.Should().Be(WindowCloseBehavior.KeepRunningInBackground);
        state.ShouldStartHidden.Should().BeTrue();
    }

    [Fact]
    public void Start_minimized_is_ignored_when_close_behavior_exits()
    {
        var state = new ApplicationBehaviorState();
        state.Apply(new ApplicationSettings
        {
            CloseBehavior = WindowCloseBehavior.ExitApplication,
            StartWithWindows = true,
            StartMinimized = true
        });

        state.ShouldStartHidden.Should().BeFalse();
    }
}
