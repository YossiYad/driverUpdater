using System.Globalization;
using System.Runtime.CompilerServices;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using Microsoft.Extensions.Logging;

namespace DriverUpdater.Services.Scanning;

public sealed class DriverScanService : IDriverScanService
{
    private const string CimV2Scope = "\\\\.\\root\\CIMV2";
    private const string SignedDriverQuery =
        "SELECT DeviceID, DeviceName, DriverVersion, DriverDate, DriverProviderName, " +
        "InfName, IsSigned, Manufacturer, DeviceClass FROM Win32_PnPSignedDriver";

    private readonly IWmiQueryRunner _wmi;
    private readonly ILogger<DriverScanService> _logger;

    public DriverScanService(IWmiQueryRunner wmi, ILogger<DriverScanService> logger)
    {
        ArgumentNullException.ThrowIfNull(wmi);
        ArgumentNullException.ThrowIfNull(logger);
        _wmi = wmi;
        _logger = logger;
    }

    public async IAsyncEnumerable<DriverInfo> ScanAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Driver scan started");
        var count = 0;

        await foreach (var row in _wmi.QueryAsync(CimV2Scope, SignedDriverQuery, cancellationToken)
                                      .ConfigureAwait(false))
        {
            if (TryProject(row, out var driver))
            {
                count++;
                yield return driver;
            }
        }

        _logger.LogInformation("Driver scan completed: {Count} drivers", count);
    }

    internal static bool TryProject(IReadOnlyDictionary<string, object?> row, out DriverInfo driver)
    {
        var deviceId = ReadString(row, "DeviceID");
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            driver = DriverInfo.Empty(string.Empty);
            return false;
        }

        var deviceClass = ReadString(row, "DeviceClass") ?? string.Empty;

        driver = new DriverInfo(
            DeviceId: deviceId,
            HardwareId: ExtractHardwareId(deviceId),
            DeviceName: ReadString(row, "DeviceName") ?? string.Empty,
            Category: MapCategory(deviceClass),
            Provider: ReadString(row, "DriverProviderName") ?? string.Empty,
            Manufacturer: ReadString(row, "Manufacturer") ?? string.Empty,
            CurrentVersion: ParseDriverVersion(ReadString(row, "DriverVersion")),
            CurrentDate: ParseDriverDate(ReadString(row, "DriverDate")),
            InfName: ReadString(row, "InfName"),
            InfPath: null,
            IsSigned: ReadBool(row, "IsSigned") ?? false,
            DeviceClass: deviceClass);

        return true;
    }

    internal static string ExtractHardwareId(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return string.Empty;
        }

        // For PCI/USB/HID style IDs, the descriptive header before the last \ is the
        // hardware ID; the segment after is the instance enumerator (e.g.
        //     PCI\VEN_1002&DEV_747E&SUBSYS_24141458&REV_FF\3&11583659&0&00
        // becomes
        //     PCI\VEN_1002&DEV_747E&SUBSYS_24141458&REV_FF).
        //
        // For software/virtual drivers enumerated under ROOT (and SWD), the descriptive
        // part is generic - dozens of unrelated drivers are reported with paths like
        // ROOT\SYSTEM\0001, ROOT\SYSTEM\0005, ... Stripping the last \ would collapse
        // them all to ROOT\SYSTEM, so an AMD chipset candidate ends up matched to a
        // Logitech G HUB row (or vice versa). Keep the full DeviceID for these so each
        // row stays uniquely indexed.
        if (deviceId.StartsWith(@"ROOT\", StringComparison.OrdinalIgnoreCase)
            || deviceId.StartsWith(@"SWD\", StringComparison.OrdinalIgnoreCase))
        {
            return deviceId;
        }

        var lastSeparator = deviceId.LastIndexOf('\\');
        return lastSeparator > 0 ? deviceId[..lastSeparator] : deviceId;
    }

    internal static DriverCategory MapCategory(string deviceClass) => deviceClass.ToUpperInvariant() switch
    {
        "DISPLAY" => DriverCategory.Display,
        "MEDIA" or "AUDIOENDPOINT" or "SOUNDCONTROLLER" => DriverCategory.Audio,
        "NET" or "NETSERVICE" or "NETTRANS" or "NETCLIENT" => DriverCategory.Network,
        "DISKDRIVE" or "VOLUME" or "SCSIADAPTER" or "STORAGE" or "HDC" or "STORAGEVOLUME" => DriverCategory.Storage,
        "SYSTEM" or "PROCESSOR" or "COMPUTER" => DriverCategory.Chipset,
        "KEYBOARD" or "MOUSE" => DriverCategory.Input,
        "PRINTER" or "PRINTQUEUE" => DriverCategory.Printer,
        "BLUETOOTH" => DriverCategory.Bluetooth,
        "CAMERA" or "IMAGE" or "IMAGINGDEVICE" => DriverCategory.Camera,
        "USB" or "USBDEVICE" => DriverCategory.Usb,
        "HIDCLASS" => DriverCategory.HumanInterface,
        "SECURITYDEVICES" or "BIOMETRIC" => DriverCategory.Security,
        "FIRMWARE" => DriverCategory.Firmware,
        "" => DriverCategory.Other,
        _ => DriverCategory.System
    };

    internal static Version? ParseDriverVersion(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }
        return Version.TryParse(raw, out var parsed) ? parsed : null;
    }

    internal static DateOnly? ParseDriverDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (raw.Length >= 8 && raw[..8].All(char.IsDigit))
        {
            if (DateOnly.TryParseExact(raw[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dmtfDate))
            {
                return dmtfDate;
            }
        }

        return DateOnly.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;
    }

    private static string? ReadString(IReadOnlyDictionary<string, object?> row, string key) =>
        row.TryGetValue(key, out var value) ? value?.ToString() : null;

    private static bool? ReadBool(IReadOnlyDictionary<string, object?> row, string key) =>
        row.TryGetValue(key, out var value) && value is bool b ? b : null;
}
