using DriverUpdater.Core.Models;

namespace DriverUpdater.Core.Abstractions;

public interface IDriverScanService
{
    IAsyncEnumerable<DriverInfo> ScanAsync(CancellationToken cancellationToken = default);
}
