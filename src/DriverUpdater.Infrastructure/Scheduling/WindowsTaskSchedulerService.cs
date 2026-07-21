using System.Diagnostics;
using System.Runtime.Versioning;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using DriverUpdater.Core.Results;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.TaskScheduler;
using SystemTask = System.Threading.Tasks.Task;

namespace DriverUpdater.Infrastructure.Scheduling;

[SupportedOSPlatform("windows")]
public sealed class WindowsTaskSchedulerService : ISchedulerService
{
    public const string TaskFolderName = "DriverUpdater";
    public const string TaskName = "PeriodicScan";
    public const string ScanOnlyArgument = "--scheduled-scan";
    public const string ScanAndUpdateArgument = "--scheduled-update";

    private readonly ILogger<WindowsTaskSchedulerService> _logger;

    public WindowsTaskSchedulerService(ILogger<WindowsTaskSchedulerService> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public System.Threading.Tasks.Task<Result<ScheduledTaskInfo?>> ApplyAsync(
        ScheduleMode mode,
        ScheduleCadence cadence,
        TimeOnly timeOfDay,
        DayOfWeek dayOfWeek,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (mode == ScheduleMode.Manual)
            {
                Remove();
                return SystemTask.FromResult(Result<ScheduledTaskInfo?>.Success(null));
            }

            using var service = new TaskService();
            EnsureFolder(service);
            var folder = service.GetFolder($"\\{TaskFolderName}");

            var definition = service.NewTask();
            definition.RegistrationInfo.Description = "DriverUpdater scheduled driver scan";
            definition.RegistrationInfo.Author = "DriverUpdater";

            definition.Principal.RunLevel = TaskRunLevel.Highest;
            definition.Principal.LogonType = TaskLogonType.InteractiveToken;

            definition.Settings.AllowDemandStart = true;
            definition.Settings.StartWhenAvailable = true;
            definition.Settings.DisallowStartIfOnBatteries = false;
            definition.Settings.StopIfGoingOnBatteries = false;
            definition.Settings.ExecutionTimeLimit = TimeSpan.FromHours(1);
            definition.Settings.WakeToRun = false;

            definition.Triggers.Add(BuildTrigger(cadence, timeOfDay, dayOfWeek));

            var exe = ResolveAppExecutablePath();
            var argument = mode == ScheduleMode.ScanAndUpdate ? ScanAndUpdateArgument : ScanOnlyArgument;
            definition.Actions.Add(new ExecAction(exe, argument));

            folder.RegisterTaskDefinition(TaskName, definition, TaskCreation.CreateOrUpdate, null, null, TaskLogonType.InteractiveToken);

            _logger.LogInformation("Registered scheduled task {Folder}\\{Task} (mode={Mode}, cadence={Cadence})",
                TaskFolderName, TaskName, mode, cadence);

            var task = folder.GetTasks().FirstOrDefault(t => t.Name == TaskName);
            var info = new ScheduledTaskInfo(
                TaskPath: $"\\{TaskFolderName}\\{TaskName}",
                Mode: mode,
                Cadence: cadence,
                TimeOfDay: timeOfDay,
                DayOfWeek: cadence == ScheduleCadence.Weekly ? dayOfWeek : null,
                NextRunAt: task?.NextRunTime is { } next && next != DateTime.MinValue ? next : null);
            return SystemTask.FromResult<Result<ScheduledTaskInfo?>>(info);
        }
        catch (UnauthorizedAccessException ex)
        {
            return SystemTask.FromResult(Result<ScheduledTaskInfo?>.Failure(ResultError.From("SCHEDULE_ACCESS_DENIED", ex)));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to register scheduled task");
            return SystemTask.FromResult(Result<ScheduledTaskInfo?>.Failure(ResultError.From("SCHEDULE_FAILED", ex)));
        }
    }

    public System.Threading.Tasks.Task<ScheduledTaskInfo?> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var service = new TaskService();
            var folder = service.GetFolder($"\\{TaskFolderName}");
            var task = folder.GetTasks().FirstOrDefault(t => t.Name == TaskName);
            if (task is null)
            {
                return SystemTask.FromResult<ScheduledTaskInfo?>(null);
            }

            var (cadence, timeOfDay, dayOfWeek) = ReadTrigger(task.Definition.Triggers);
            var mode = task.Definition.Actions
                .OfType<ExecAction>()
                .FirstOrDefault()?.Arguments switch
            {
                ScanAndUpdateArgument => ScheduleMode.ScanAndUpdate,
                ScanOnlyArgument => ScheduleMode.ScanOnly,
                _ => ScheduleMode.Manual
            };

            return SystemTask.FromResult<ScheduledTaskInfo?>(new ScheduledTaskInfo(
                TaskPath: $"\\{TaskFolderName}\\{TaskName}",
                Mode: mode,
                Cadence: cadence,
                TimeOfDay: timeOfDay,
                DayOfWeek: cadence == ScheduleCadence.Weekly ? dayOfWeek : null,
                NextRunAt: task.NextRunTime != DateTime.MinValue ? task.NextRunTime : null));
        }
        catch (FileNotFoundException)
        {
            return SystemTask.FromResult<ScheduledTaskInfo?>(null);
        }
        catch (DirectoryNotFoundException)
        {
            return SystemTask.FromResult<ScheduledTaskInfo?>(null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read scheduled task");
            return SystemTask.FromResult<ScheduledTaskInfo?>(null);
        }
    }

    public SystemTask RemoveAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Remove();
        return SystemTask.CompletedTask;
    }

    private void Remove()
    {
        try
        {
            using var service = new TaskService();
            var folder = service.RootFolder.SubFolders.FirstOrDefault(f => f.Name == TaskFolderName);
            if (folder is null)
            {
                return;
            }
            if (folder.GetTasks().Any(t => t.Name == TaskName))
            {
                folder.DeleteTask(TaskName, exceptionOnNotExists: false);
                _logger.LogInformation("Removed scheduled task {Folder}\\{Task}", TaskFolderName, TaskName);
            }
            if (folder.GetTasks().Count == 0)
            {
                service.RootFolder.DeleteFolder(TaskFolderName, exceptionOnNotExists: false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not remove scheduled task");
        }
    }

    internal static Trigger BuildTrigger(ScheduleCadence cadence, TimeOnly timeOfDay, DayOfWeek dayOfWeek)
    {
        var startBoundary = DateTime.Today.Add(timeOfDay.ToTimeSpan());
        if (startBoundary <= DateTime.Now)
        {
            startBoundary = startBoundary.AddDays(1);
        }

        return cadence switch
        {
            ScheduleCadence.Daily => new DailyTrigger { DaysInterval = 1, StartBoundary = startBoundary },
            ScheduleCadence.Weekly => new WeeklyTrigger
            {
                DaysOfWeek = MapDay(dayOfWeek),
                WeeksInterval = 1,
                StartBoundary = startBoundary
            },
            ScheduleCadence.Monthly => new MonthlyTrigger
            {
                MonthsOfYear = MonthsOfTheYear.AllMonths,
                DaysOfMonth = new[] { 1 },
                StartBoundary = startBoundary
            },
            _ => throw new ArgumentOutOfRangeException(nameof(cadence), cadence, null)
        };
    }

    internal static (ScheduleCadence Cadence, TimeOnly TimeOfDay, DayOfWeek DayOfWeek) ReadTrigger(TriggerCollection triggers)
    {
        foreach (var trigger in triggers)
        {
            switch (trigger)
            {
                case DailyTrigger daily:
                    return (ScheduleCadence.Daily, TimeOnly.FromDateTime(daily.StartBoundary), System.DayOfWeek.Sunday);
                case WeeklyTrigger weekly:
                    return (ScheduleCadence.Weekly,
                        TimeOnly.FromDateTime(weekly.StartBoundary),
                        UnmapDay(weekly.DaysOfWeek));
                case MonthlyTrigger monthly:
                    return (ScheduleCadence.Monthly, TimeOnly.FromDateTime(monthly.StartBoundary), System.DayOfWeek.Sunday);
            }
        }
        return (ScheduleCadence.Daily, new TimeOnly(9, 0), System.DayOfWeek.Sunday);
    }

    internal static DaysOfTheWeek MapDay(DayOfWeek day) => day switch
    {
        System.DayOfWeek.Sunday => DaysOfTheWeek.Sunday,
        System.DayOfWeek.Monday => DaysOfTheWeek.Monday,
        System.DayOfWeek.Tuesday => DaysOfTheWeek.Tuesday,
        System.DayOfWeek.Wednesday => DaysOfTheWeek.Wednesday,
        System.DayOfWeek.Thursday => DaysOfTheWeek.Thursday,
        System.DayOfWeek.Friday => DaysOfTheWeek.Friday,
        System.DayOfWeek.Saturday => DaysOfTheWeek.Saturday,
        _ => DaysOfTheWeek.Monday
    };

    internal static DayOfWeek UnmapDay(DaysOfTheWeek days)
    {
        if (days.HasFlag(DaysOfTheWeek.Sunday)) { return System.DayOfWeek.Sunday; }
        if (days.HasFlag(DaysOfTheWeek.Monday)) { return System.DayOfWeek.Monday; }
        if (days.HasFlag(DaysOfTheWeek.Tuesday)) { return System.DayOfWeek.Tuesday; }
        if (days.HasFlag(DaysOfTheWeek.Wednesday)) { return System.DayOfWeek.Wednesday; }
        if (days.HasFlag(DaysOfTheWeek.Thursday)) { return System.DayOfWeek.Thursday; }
        if (days.HasFlag(DaysOfTheWeek.Friday)) { return System.DayOfWeek.Friday; }
        if (days.HasFlag(DaysOfTheWeek.Saturday)) { return System.DayOfWeek.Saturday; }
        return System.DayOfWeek.Monday;
    }

    private static void EnsureFolder(TaskService service)
    {
        if (service.RootFolder.SubFolders.All(f => f.Name != TaskFolderName))
        {
            service.RootFolder.CreateFolder(TaskFolderName);
        }
    }

    private static string ResolveAppExecutablePath()
    {
        var current = Process.GetCurrentProcess().MainModule?.FileName;
        if (!string.IsNullOrEmpty(current) && File.Exists(current))
        {
            return current;
        }
        return Path.Combine(AppContext.BaseDirectory, "DriverUpdater.exe");
    }
}
