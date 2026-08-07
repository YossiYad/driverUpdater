using System.Runtime.CompilerServices;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using Microsoft.Extensions.Logging;

namespace DriverUpdater.Services.Scanning;

/// <summary>
/// Decorates the real scan service so every completed scan records the observed driver versions
/// into <see cref="IDriverVersionHistoryStore"/>. This is the single choke point through which
/// both the UI and the scheduled runner scan, so version history accumulates everywhere without
/// either caller knowing about it. A cancelled or failed scan records nothing - partial
/// snapshots would make it look like devices disappeared.
/// </summary>
public sealed class VersionRecordingDriverScanService : IDriverScanService
{
    private readonly IDriverScanService _inner;
    private readonly IDriverVersionHistoryStore _versionHistory;
    private readonly ILogger<VersionRecordingDriverScanService> _logger;

    public VersionRecordingDriverScanService(
        IDriverScanService inner,
        IDriverVersionHistoryStore versionHistory,
        ILogger<VersionRecordingDriverScanService> logger)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(versionHistory);
        ArgumentNullException.ThrowIfNull(logger);
        _inner = inner;
        _versionHistory = versionHistory;
        _logger = logger;
    }

    public async IAsyncEnumerable<DriverInfo> ScanAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var snapshot = new List<DriverInfo>();
        await foreach (var driver in _inner.ScanAsync(cancellationToken).ConfigureAwait(false))
        {
            snapshot.Add(driver);
            yield return driver;
        }

        try
        {
            await _versionHistory.RecordScanAsync(snapshot, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Recorded driver version history for {Count} scanned drivers", snapshot.Count);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // History is a convenience; failing to write it must never fail the scan.
            _logger.LogWarning(ex, "Could not record driver version history for this scan");
        }
    }
}
