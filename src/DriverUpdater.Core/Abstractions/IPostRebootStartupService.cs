namespace DriverUpdater.Core.Abstractions;

public interface IPostRebootStartupService
{
    Task RegisterAsync(CancellationToken cancellationToken = default);

    Task UnregisterAsync(CancellationToken cancellationToken = default);
}
