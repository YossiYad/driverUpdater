namespace DriverUpdater.App.Services;

public interface IAppUpdater
{
    Task CheckAndApplyAsync(CancellationToken cancellationToken = default);
}
