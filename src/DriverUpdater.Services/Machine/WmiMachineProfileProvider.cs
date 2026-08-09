using System.Globalization;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using Microsoft.Extensions.Logging;

namespace DriverUpdater.Services.Machine;

public sealed class WmiMachineProfileProvider : IMachineProfileProvider
{
    private const string CimV2Scope = "\\\\.\\root\\CIMV2";
    private const string ComputerSystemQuery =
        "SELECT Manufacturer, Model, SystemFamily, SystemSKUNumber, SystemType, TotalPhysicalMemory FROM Win32_ComputerSystem";
    private const string BaseBoardQuery = "SELECT Manufacturer, Product, Version FROM Win32_BaseBoard";
    private const string BiosQuery = "SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS";
    private const string ProcessorQuery = "SELECT Name, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor";
    private const string VideoControllerQuery = "SELECT Name, AdapterCompatibility FROM Win32_VideoController";
    private const string OperatingSystemQuery =
        "SELECT Caption, Version, BuildNumber, OSArchitecture FROM Win32_OperatingSystem";

    private readonly IWmiQueryRunner _wmi;
    private readonly ILogger<WmiMachineProfileProvider> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private MachineProfile? _cached;

    public WmiMachineProfileProvider(IWmiQueryRunner wmi, ILogger<WmiMachineProfileProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(wmi);
        ArgumentNullException.ThrowIfNull(logger);
        _wmi = wmi;
        _logger = logger;
    }

    public async Task<MachineProfile> GetAsync(CancellationToken cancellationToken = default)
    {
        if (_cached is not null)
        {
            return _cached;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cached is not null)
            {
                return _cached;
            }

            _cached = await ReadAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Machine profile detected:{NewLine}{Profile}",
                Environment.NewLine,
                _cached.HasAnyDetail ? _cached.Describe() : "- <nothing readable>");
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<MachineProfile> ReadAsync(CancellationToken cancellationToken)
    {
        var computerSystem = await FirstRowAsync(ComputerSystemQuery, cancellationToken).ConfigureAwait(false);
        var baseBoard = await FirstRowAsync(BaseBoardQuery, cancellationToken).ConfigureAwait(false);
        var bios = await FirstRowAsync(BiosQuery, cancellationToken).ConfigureAwait(false);
        var processor = await FirstRowAsync(ProcessorQuery, cancellationToken).ConfigureAwait(false);
        var operatingSystem = await FirstRowAsync(OperatingSystemQuery, cancellationToken).ConfigureAwait(false);

        var adapters = new List<string>();
        var seenAdapters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            await foreach (var row in _wmi.QueryAsync(CimV2Scope, VideoControllerQuery, cancellationToken).ConfigureAwait(false))
            {
                var name = Text(row, "Name");
                // Windows lists the same adapter once per attached output.
                if (string.IsNullOrWhiteSpace(name) || !seenAdapters.Add(name))
                {
                    continue;
                }

                adapters.Add(Decorate(name, Text(row, "AdapterCompatibility")));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not enumerate display adapters for the machine profile");
        }

        return new MachineProfile(
            SystemManufacturer: Text(computerSystem, "Manufacturer"),
            SystemModel: Text(computerSystem, "Model"),
            SystemFamily: Text(computerSystem, "SystemFamily"),
            SystemSku: Text(computerSystem, "SystemSKUNumber"),
            BaseBoardManufacturer: Text(baseBoard, "Manufacturer"),
            BaseBoardProduct: Text(baseBoard, "Product"),
            BaseBoardVersion: Text(baseBoard, "Version"),
            BiosManufacturer: Text(bios, "Manufacturer"),
            BiosVersion: Text(bios, "SMBIOSBIOSVersion"),
            BiosReleaseDate: ParseCimDate(Text(bios, "ReleaseDate")),
            ProcessorName: Text(processor, "Name"),
            ProcessorCores: Number(processor, "NumberOfCores"),
            ProcessorLogicalProcessors: Number(processor, "NumberOfLogicalProcessors"),
            GraphicsAdapters: adapters,
            TotalPhysicalMemoryBytes: LongNumber(computerSystem, "TotalPhysicalMemory"),
            OperatingSystemName: Text(operatingSystem, "Caption"),
            OperatingSystemVersion: Text(operatingSystem, "Version"),
            OperatingSystemBuild: Text(operatingSystem, "BuildNumber"),
            OperatingSystemArchitecture: Text(operatingSystem, "OSArchitecture"),
            SystemType: Text(computerSystem, "SystemType"));
    }

    private async Task<IReadOnlyDictionary<string, object?>?> FirstRowAsync(
        string query,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var row in _wmi.QueryAsync(CimV2Scope, query, cancellationToken).ConfigureAwait(false))
            {
                return row;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Machine profile query failed: {Query}", query);
        }

        return null;
    }

    // "Intel(R) Iris(R) Xe Graphics" already says Intel, so appending "(Intel Corporation)"
    // only pads the prompt. Match on the vendor's leading word, which is the brand.
    private static string Decorate(string name, string? vendor)
    {
        if (string.IsNullOrWhiteSpace(vendor))
        {
            return name;
        }

        var brand = vendor.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        return string.IsNullOrEmpty(brand) || name.Contains(brand, StringComparison.OrdinalIgnoreCase)
            ? name
            : $"{name} ({vendor})";
    }

    private static string? Text(IReadOnlyDictionary<string, object?>? row, string column) =>
        row is not null && row.TryGetValue(column, out var value) ? value?.ToString() : null;

    private static int? Number(IReadOnlyDictionary<string, object?>? row, string column) =>
        int.TryParse(Text(row, column), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    private static long? LongNumber(IReadOnlyDictionary<string, object?>? row, string column) =>
        long.TryParse(Text(row, column), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    // CIM_DATETIME: yyyyMMddHHmmss.ffffff+UUU. Only the date part is meaningful for a BIOS.
    internal static DateOnly? ParseCimDate(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length >= 8
        && DateOnly.TryParseExact(value[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;
}
