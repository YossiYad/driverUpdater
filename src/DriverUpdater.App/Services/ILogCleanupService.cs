using DriverUpdater.Core.Options;

namespace DriverUpdater.App.Services;

public interface ILogCleanupService
{
    string LogDirectory { get; }

    Task<int> CleanupAsync(
        LogCleanupSettings settings,
        CancellationToken cancellationToken = default);
}
