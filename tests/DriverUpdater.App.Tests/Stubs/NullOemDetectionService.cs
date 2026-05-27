using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;

namespace DriverUpdater.App.Tests.Stubs;

public sealed class NullOemDetectionService : IOemDetectionService
{
    public Task<OemInfo?> DetectAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<OemInfo?>(null);
}
