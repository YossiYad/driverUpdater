using DriverUpdater.Core.Models;

namespace DriverUpdater.Core.Abstractions;

public interface IUpdateSource
{
    UpdateSource Kind { get; }

    string DisplayName { get; }

    IAsyncEnumerable<UpdateCandidate> SearchAsync(
        IReadOnlyCollection<DriverInfo> drivers,
        CancellationToken cancellationToken = default);
}
