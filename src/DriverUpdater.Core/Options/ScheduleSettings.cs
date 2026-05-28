using DriverUpdater.Core.Models;

namespace DriverUpdater.Core.Options;

public sealed class ScheduleSettings
{
    public const string SectionName = "Schedule";

    public ScheduleMode Mode { get; set; } = ScheduleMode.Manual;

    public ScheduleCadence Cadence { get; set; } = ScheduleCadence.Weekly;

    public TimeOnly TimeOfDay { get; set; } = new(9, 0);

    public DayOfWeek DayOfWeek { get; set; } = System.DayOfWeek.Monday;
}
