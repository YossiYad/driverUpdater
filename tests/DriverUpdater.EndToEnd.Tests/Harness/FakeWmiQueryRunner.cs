using DriverUpdater.Core.Abstractions;

namespace DriverUpdater.EndToEnd.Tests.Harness;

/// <summary>
/// Replays canned Win32_PnPSignedDriver rows in the exact shape the real WMI runner produces
/// (DMTF datetime strings, string[] hardware ids, boxed booleans), so the production
/// DriverScanService projection runs unmodified.
/// </summary>
public sealed class FakeWmiQueryRunner : IWmiQueryRunner
{
    private readonly Dictionary<string, List<IReadOnlyDictionary<string, object?>>> _rowsByQueryFragment =
        new(StringComparer.OrdinalIgnoreCase);

    public List<string> ExecutedQueries { get; } = new();

    public FakeWmiQueryRunner WithRows(string queryFragment, params IReadOnlyDictionary<string, object?>[] rows)
    {
        if (!_rowsByQueryFragment.TryGetValue(queryFragment, out var bucket))
        {
            bucket = new List<IReadOnlyDictionary<string, object?>>();
            _rowsByQueryFragment[queryFragment] = bucket;
        }
        bucket.AddRange(rows);
        return this;
    }

    public FakeWmiQueryRunner WithSignedDrivers(params IReadOnlyDictionary<string, object?>[] rows) =>
        WithRows("Win32_PnPSignedDriver", rows);

    public async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> QueryAsync(
        string scope,
        string wqlQuery,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ExecutedQueries.Add(wqlQuery);

        foreach (var (fragment, rows) in _rowsByQueryFragment)
        {
            if (!wqlQuery.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return row;
            }
        }
    }

    /// <summary>Builds a row using the same column names and value types the real WMI provider returns.</summary>
    public static IReadOnlyDictionary<string, object?> SignedDriverRow(
        string deviceId,
        string deviceName,
        string driverVersion,
        string driverDateDmtf,
        string providerName,
        string deviceClass,
        string manufacturer = "",
        string? infName = null,
        bool isSigned = true,
        string[]? hardwareIds = null,
        string[]? compatIds = null) =>
        new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["DeviceID"] = deviceId,
            ["DeviceName"] = deviceName,
            ["DriverVersion"] = driverVersion,
            ["DriverDate"] = driverDateDmtf,
            ["DriverProviderName"] = providerName,
            ["InfName"] = infName,
            ["IsSigned"] = isSigned,
            ["Manufacturer"] = manufacturer,
            ["DeviceClass"] = deviceClass,
            ["HardWareID"] = hardwareIds ?? Array.Empty<string>(),
            ["CompatID"] = compatIds ?? Array.Empty<string>()
        };

    /// <summary>Formats a date the way WMI reports DriverDate (DMTF datetime).</summary>
    public static string Dmtf(int year, int month, int day) =>
        $"{year:0000}{month:00}{day:00}000000.000000+000";
}
