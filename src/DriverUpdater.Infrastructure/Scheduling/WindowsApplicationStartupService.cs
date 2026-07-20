using System.Diagnostics;
using System.Runtime.Versioning;
using DriverUpdater.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.TaskScheduler;
using SystemTask = System.Threading.Tasks.Task;

namespace DriverUpdater.Infrastructure.Scheduling;

[SupportedOSPlatform("windows")]
public sealed class WindowsApplicationStartupService : IApplicationStartupService
{
    public const string TaskName = "StartWithWindows";
    public const string BackgroundArgument = "--background";
    private readonly ILogger<WindowsApplicationStartupService> _logger;

    public WindowsApplicationStartupService(ILogger<WindowsApplicationStartupService> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public SystemTask ApplyAsync(
        bool startWithWindows,
        bool startMinimized,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!startWithWindows)
        {
            Remove();
            return SystemTask.CompletedTask;
        }

        using var service = new TaskService();
        EnsureFolder(service);
        var folder = service.GetFolder($"\\{WindowsTaskSchedulerService.TaskFolderName}");
        var definition = service.NewTask();
        definition.RegistrationInfo.Description = "Start DriverUpdater when the current user signs in";
        definition.RegistrationInfo.Author = "DriverUpdater";
        definition.Principal.RunLevel = TaskRunLevel.Highest;
        definition.Principal.LogonType = TaskLogonType.InteractiveToken;
        definition.Settings.AllowDemandStart = true;
        definition.Settings.StartWhenAvailable = true;
        definition.Settings.DisallowStartIfOnBatteries = false;
        definition.Settings.StopIfGoingOnBatteries = false;
        definition.Settings.ExecutionTimeLimit = TimeSpan.Zero;
        definition.Triggers.Add(new LogonTrigger());

        var executablePath = ResolveAppExecutablePath();
        definition.Actions.Add(new ExecAction(
            executablePath,
            BuildArguments(startMinimized),
            Path.GetDirectoryName(executablePath)));
        folder.RegisterTaskDefinition(
            TaskName,
            definition,
            TaskCreation.CreateOrUpdate,
            null,
            null,
            TaskLogonType.InteractiveToken);

        _logger.LogInformation(
            "Registered DriverUpdater Windows startup task: path={Path}, startMinimized={StartMinimized}",
            executablePath,
            startMinimized);
        return SystemTask.CompletedTask;
    }

    internal static string BuildArguments(bool startMinimized) =>
        startMinimized ? BackgroundArgument : string.Empty;

    private void Remove()
    {
        try
        {
            using var service = new TaskService();
            var folder = service.RootFolder.SubFolders.FirstOrDefault(
                item => item.Name == WindowsTaskSchedulerService.TaskFolderName);
            if (folder is null)
            {
                return;
            }

            folder.DeleteTask(TaskName, exceptionOnNotExists: false);
            _logger.LogInformation("Removed DriverUpdater Windows startup task");
            if (folder.GetTasks().Count == 0)
            {
                service.RootFolder.DeleteFolder(
                    WindowsTaskSchedulerService.TaskFolderName,
                    exceptionOnNotExists: false);
            }
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private static void EnsureFolder(TaskService service)
    {
        if (service.RootFolder.SubFolders.All(
            folder => folder.Name != WindowsTaskSchedulerService.TaskFolderName))
        {
            service.RootFolder.CreateFolder(WindowsTaskSchedulerService.TaskFolderName);
        }
    }

    private static string ResolveAppExecutablePath()
    {
        var current = Process.GetCurrentProcess().MainModule?.FileName;
        if (!string.IsNullOrWhiteSpace(current) && File.Exists(current))
        {
            return current;
        }
        return Path.Combine(AppContext.BaseDirectory, "DriverUpdater.exe");
    }
}
