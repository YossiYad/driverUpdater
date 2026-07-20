namespace DriverUpdater.Core.Abstractions;

public interface IApplicationStartupService
{
    Task ApplyAsync(
        bool startWithWindows,
        bool startMinimized,
        CancellationToken cancellationToken = default);
}
