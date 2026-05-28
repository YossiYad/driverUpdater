using DriverUpdater.Core.Models;
using DriverUpdater.Infrastructure.Scheduling;
using FluentAssertions;
using Microsoft.Win32.TaskScheduler;

namespace DriverUpdater.Infrastructure.Tests.Scheduling;

public class WindowsTaskSchedulerServiceTests
{
    [Theory]
    [InlineData(ScheduleCadence.Daily)]
    [InlineData(ScheduleCadence.Monthly)]
    public void BuildTrigger_returns_matching_trigger_type(ScheduleCadence cadence)
    {
        var trigger = WindowsTaskSchedulerService.BuildTrigger(cadence, new TimeOnly(9, 30), DayOfWeek.Monday);

        switch (cadence)
        {
            case ScheduleCadence.Daily:
                trigger.Should().BeOfType<DailyTrigger>();
                break;
            case ScheduleCadence.Monthly:
                trigger.Should().BeOfType<MonthlyTrigger>();
                break;
        }
        trigger.StartBoundary.TimeOfDay.Should().Be(new TimeSpan(9, 30, 0));
    }

    [Fact]
    public void BuildTrigger_weekly_uses_provided_day_of_week()
    {
        var trigger = (WeeklyTrigger)WindowsTaskSchedulerService.BuildTrigger(ScheduleCadence.Weekly, new TimeOnly(10, 0), DayOfWeek.Wednesday);

        trigger.DaysOfWeek.Should().Be(DaysOfTheWeek.Wednesday);
        trigger.WeeksInterval.Should().Be(1);
    }

    [Fact]
    public void BuildTrigger_pushes_start_boundary_to_tomorrow_when_time_already_passed()
    {
        var pastTime = TimeOnly.FromDateTime(DateTime.Now).AddHours(-1);
        var trigger = WindowsTaskSchedulerService.BuildTrigger(ScheduleCadence.Daily, pastTime, DayOfWeek.Monday);

        trigger.StartBoundary.Should().BeAfter(DateTime.Now);
    }

    [Theory]
    [InlineData(DayOfWeek.Monday, DaysOfTheWeek.Monday)]
    [InlineData(DayOfWeek.Tuesday, DaysOfTheWeek.Tuesday)]
    [InlineData(DayOfWeek.Friday, DaysOfTheWeek.Friday)]
    [InlineData(DayOfWeek.Sunday, DaysOfTheWeek.Sunday)]
    public void MapDay_maps_each_day_of_week(DayOfWeek input, DaysOfTheWeek expected)
    {
        WindowsTaskSchedulerService.MapDay(input).Should().Be(expected);
    }

    [Theory]
    [InlineData(DaysOfTheWeek.Monday, DayOfWeek.Monday)]
    [InlineData(DaysOfTheWeek.Saturday, DayOfWeek.Saturday)]
    public void UnmapDay_inverts_the_mapping(DaysOfTheWeek input, DayOfWeek expected)
    {
        WindowsTaskSchedulerService.UnmapDay(input).Should().Be(expected);
    }
}
