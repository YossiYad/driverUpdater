namespace DriverUpdater.Core.Abstractions;

public interface IInstallExecutionGate
{
    ValueTask<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken = default);
}
