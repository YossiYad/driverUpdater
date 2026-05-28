using DriverUpdater.Core.Models;
using DriverUpdater.Core.Results;

namespace DriverUpdater.Core.Abstractions;

public interface ISchedulerService
{
    Task<Result<ScheduledTaskInfo?>> ApplyAsync(
        ScheduleMode mode,
        ScheduleCadence cadence,
        TimeOnly timeOfDay,
        DayOfWeek dayOfWeek,
        CancellationToken cancellationToken = default);

    Task<ScheduledTaskInfo?> GetCurrentAsync(CancellationToken cancellationToken = default);

    Task RemoveAsync(CancellationToken cancellationToken = default);
}
