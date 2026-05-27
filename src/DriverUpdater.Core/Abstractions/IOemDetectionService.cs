using DriverUpdater.Core.Models;

namespace DriverUpdater.Core.Abstractions;

public interface IOemDetectionService
{
    Task<OemInfo?> DetectAsync(CancellationToken cancellationToken = default);
}
