namespace DriverUpdater.Core.Models;

public sealed record ScheduledTaskInfo(
    string TaskPath,
    ScheduleMode Mode,
    ScheduleCadence Cadence,
    TimeOnly TimeOfDay,
    DayOfWeek? DayOfWeek,
    DateTimeOffset? NextRunAt);
