using DriverUpdater.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace DriverUpdater.Infrastructure.Scheduling;

public sealed class WindowsPostRebootStartupService : IPostRebootStartupService
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "DriverUpdaterPostRebootVerification";
    public const string LaunchArgument = "--verify-after-reboot";

    private readonly ILogger<WindowsPostRebootStartupService> _logger;

    public WindowsPostRebootStartupService(ILogger<WindowsPostRebootStartupService> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public Task RegisterAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            throw new InvalidOperationException("The application executable path is unavailable.");
        }

        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("Could not open the current user's startup registry key.");
        key.SetValue(ValueName, BuildCommand(processPath), RegistryValueKind.String);
        _logger.LogInformation("Registered post-reboot verification startup for {Path}", processPath);
        return Task.CompletedTask;
    }

    public Task UnregisterAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
        _logger.LogInformation("Removed post-reboot verification startup registration");
        return Task.CompletedTask;
    }

    internal static string BuildCommand(string processPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processPath);
        return $"\"{processPath}\" {LaunchArgument}";
    }
}
