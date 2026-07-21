using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using Microsoft.Extensions.Logging;

namespace DriverUpdater.Services.Scanning;

/// <summary>
/// Reads back a single device's current driver via WMI (Win32_PnPSignedDriver), so the
/// install pipeline can confirm the active driver actually changed after an install.
/// </summary>
public sealed class WmiInstalledDriverProbe : IInstalledDriverProbe
{
    private const string CimV2Scope = "\\\\.\\root\\CIMV2";

    private readonly IWmiQueryRunner _wmi;
    private readonly ILogger<WmiInstalledDriverProbe> _logger;

    public WmiInstalledDriverProbe(IWmiQueryRunner wmi, ILogger<WmiInstalledDriverProbe> logger)
    {
        ArgumentNullException.ThrowIfNull(wmi);
        ArgumentNullException.ThrowIfNull(logger);
        _wmi = wmi;
        _logger = logger;
    }

    public async Task<InstalledDriverState?> GetCurrentAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return null;
        }

        // DeviceID contains backslashes; escape them for the WQL string literal.
        var escaped = deviceId.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "''", StringComparison.Ordinal);
        var query =
            "SELECT DriverVersion, DriverDate FROM Win32_PnPSignedDriver WHERE DeviceID='" + escaped + "'";

        try
        {
            await foreach (var row in _wmi.QueryAsync(CimV2Scope, query, cancellationToken).ConfigureAwait(false))
            {
                var version = DriverScanService.ParseDriverVersion(ReadString(row, "DriverVersion"));
                var date = DriverScanService.ParseDriverDate(ReadString(row, "DriverDate"));
                return new InstalledDriverState(version, date);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Post-install driver probe failed for {DeviceId}", deviceId);
            return null;
        }

        return null;
    }

    private static string? ReadString(IReadOnlyDictionary<string, object?> row, string key) =>
        row.TryGetValue(key, out var value) ? value?.ToString() : null;
}
