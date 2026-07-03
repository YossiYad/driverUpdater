using System.Runtime.CompilerServices;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using DriverUpdater.Services.Scanning;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriverUpdater.Services.Tests.Scanning;

public class DriverScanServiceTests
{
    [Fact]
    public async Task ScanAsync_yields_one_driver_per_wmi_row()
    {
        var rows = new[]
        {
            Row("PCI\\VEN_8086&DEV_1234&REV_01\\3&11&0&A0", "Intel HD Graphics", "Display", "10.0.19041.4291", "20240306000000.******+***", "Intel", "Intel Corporation", "oem1.inf", true),
            Row("USB\\VID_046D&PID_C52B\\5&abc&0&1", "Logitech Receiver", "USB", "1.0.0.0", "20210101000000.******+***", "Logitech", "Logitech", "oem99.inf", true)
        };

        var service = NewService(rows);
        var collected = new List<DriverInfo>();

        await foreach (var driver in service.ScanAsync())
        {
            collected.Add(driver);
        }

        collected.Should().HaveCount(2);
        collected[0].DeviceName.Should().Be("Intel HD Graphics");
        collected[0].Category.Should().Be(DriverCategory.Display);
        collected[0].CurrentVersion.Should().Be(new Version(10, 0, 19041, 4291));
        collected[0].CurrentDate.Should().Be(new DateOnly(2024, 3, 6));
        collected[0].HardwareId.Should().Be("PCI\\VEN_8086&DEV_1234&REV_01");
        collected[0].HardwareIds.Should().Contain("PCI\\VEN_8086&DEV_1234&REV_01");
        collected[1].Category.Should().Be(DriverCategory.Usb);
    }

    [Fact]
    public void TryProject_captures_hardware_and_compatible_ids_for_update_searches()
    {
        var row = Row(
            "PCI\\VEN_8086&DEV_1234&REV_01\\3&11&0&A0",
            "Intel Device",
            "System",
            "1.0.0.0",
            "20240306000000.******+***",
            "Intel",
            "Intel Corporation",
            "oem1.inf",
            true);
        var mutable = row.ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);
        mutable["HardWareID"] = new[]
        {
            "PCI\\VEN_8086&DEV_1234&SUBSYS_00000000&REV_01",
            "PCI\\VEN_8086&DEV_1234&SUBSYS_00000000"
        };
        mutable["CompatID"] = new[] { "PCI\\VEN_8086&DEV_1234" };

        DriverScanService.TryProject(mutable, out var driver).Should().BeTrue();

        driver.HardwareIds.Should().BeEquivalentTo([
            "PCI\\VEN_8086&DEV_1234&REV_01",
            "PCI\\VEN_8086&DEV_1234&SUBSYS_00000000&REV_01",
            "PCI\\VEN_8086&DEV_1234&SUBSYS_00000000",
            "PCI\\VEN_8086&DEV_1234"
        ], options => options.WithStrictOrdering());
    }

    [Fact]
    public async Task ScanAsync_skips_rows_with_missing_device_id()
    {
        var rows = new[]
        {
            Row(null, "Anonymous", "Display", "1.0.0.0", null, null, null, null, false),
            Row("VALID\\ID\\1", "Real", "Display", "1.0.0.0", null, null, null, null, false)
        };

        var service = NewService(rows);
        var collected = new List<DriverInfo>();

        await foreach (var driver in service.ScanAsync())
        {
            collected.Add(driver);
        }

        collected.Should().ContainSingle();
        collected[0].DeviceName.Should().Be("Real");
    }

    [Fact]
    public async Task ScanAsync_passes_cancellation_token_to_wmi()
    {
        var fake = new FakeWmiRunner(Array.Empty<IReadOnlyDictionary<string, object?>>());
        var service = new DriverScanService(fake, NullLogger<DriverScanService>.Instance);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () =>
        {
            await foreach (var _ in service.ScanAsync(cts.Token)) { }
        };

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Theory]
    [InlineData("Display", DriverCategory.Display)]
    [InlineData("DISPLAY", DriverCategory.Display)]
    [InlineData("Media", DriverCategory.Audio)]
    [InlineData("AudioEndpoint", DriverCategory.Audio)]
    [InlineData("Net", DriverCategory.Network)]
    [InlineData("DiskDrive", DriverCategory.Storage)]
    [InlineData("SCSIAdapter", DriverCategory.Storage)]
    [InlineData("System", DriverCategory.Chipset)]
    [InlineData("Keyboard", DriverCategory.Input)]
    [InlineData("Mouse", DriverCategory.Input)]
    [InlineData("Printer", DriverCategory.Printer)]
    [InlineData("Bluetooth", DriverCategory.Bluetooth)]
    [InlineData("Camera", DriverCategory.Camera)]
    [InlineData("USB", DriverCategory.Usb)]
    [InlineData("HIDClass", DriverCategory.HumanInterface)]
    [InlineData("Firmware", DriverCategory.Firmware)]
    [InlineData("Biometric", DriverCategory.Security)]
    [InlineData("UnknownClass", DriverCategory.System)]
    [InlineData("", DriverCategory.Other)]
    public void MapCategory_translates_device_class_strings(string deviceClass, DriverCategory expected)
    {
        DriverScanService.MapCategory(deviceClass).Should().Be(expected);
    }

    [Theory]
    [InlineData("10.0.19041.4291", "10.0.19041.4291")]
    [InlineData("1.0", "1.0")]
    [InlineData("not a version", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ParseDriverVersion_handles_dmtf_and_invalid_values(string? raw, string? expected)
    {
        var result = DriverScanService.ParseDriverVersion(raw);
        result?.ToString().Should().Be(expected);
    }

    [Fact]
    public void ParseDriverDate_handles_dmtf_format()
    {
        DriverScanService.ParseDriverDate("20240306000000.******+***")
            .Should().Be(new DateOnly(2024, 3, 6));
    }

    [Fact]
    public void ParseDriverDate_returns_null_for_garbage()
    {
        DriverScanService.ParseDriverDate("not a date").Should().BeNull();
        DriverScanService.ParseDriverDate("").Should().BeNull();
        DriverScanService.ParseDriverDate(null).Should().BeNull();
    }

    [Theory]
    // PCI / USB / HID: the descriptive header is the hardware ID, the segment after
    // the last \ is the instance enumerator and gets stripped.
    [InlineData("PCI\\VEN_8086&DEV_1234&REV_01\\3&11&0&A0", "PCI\\VEN_8086&DEV_1234&REV_01")]
    [InlineData("USB\\VID_046D&PID_C52B\\5&abc&0&1", "USB\\VID_046D&PID_C52B")]
    [InlineData("HID\\VID_046D&PID_C547&MI_03\\7&abcd&0&0001", "HID\\VID_046D&PID_C547&MI_03")]
    // ROOT / SWD: stripping the last segment would collapse different software/virtual
    // drivers (AMD Special Tools at ROOT\SYSTEM\0001, Logitech G HUB at
    // ROOT\SYSTEM\0005, ...) onto the same key. Keep the instance number so each row
    // stays uniquely indexable.
    [InlineData("ROOT\\BASICDISPLAY\\0000", "ROOT\\BASICDISPLAY\\0000")]
    [InlineData("ROOT\\SYSTEM\\0001", "ROOT\\SYSTEM\\0001")]
    [InlineData("ROOT\\SYSTEM\\0005", "ROOT\\SYSTEM\\0005")]
    [InlineData("SWD\\PRINTENUM\\PrintQueues", "SWD\\PRINTENUM\\PrintQueues")]
    // Pathological inputs are returned as-is.
    [InlineData("NOSLASH", "NOSLASH")]
    [InlineData("", "")]
    public void ExtractHardwareId_strips_location_suffix(string deviceId, string expected)
    {
        DriverScanService.ExtractHardwareId(deviceId).Should().Be(expected);
    }

    private static DriverScanService NewService(IEnumerable<IReadOnlyDictionary<string, object?>> rows)
    {
        var fake = new FakeWmiRunner(rows);
        return new DriverScanService(fake, NullLogger<DriverScanService>.Instance);
    }

    private static IReadOnlyDictionary<string, object?> Row(
        string? deviceId,
        string? deviceName,
        string? deviceClass,
        string? driverVersion,
        string? driverDate,
        string? provider,
        string? manufacturer,
        string? infName,
        bool isSigned) => new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["DeviceID"] = deviceId,
            ["DeviceName"] = deviceName,
            ["DeviceClass"] = deviceClass,
            ["DriverVersion"] = driverVersion,
            ["DriverDate"] = driverDate,
            ["DriverProviderName"] = provider,
            ["Manufacturer"] = manufacturer,
            ["InfName"] = infName,
            ["IsSigned"] = isSigned
        };

    private sealed class FakeWmiRunner : IWmiQueryRunner
    {
        private readonly IEnumerable<IReadOnlyDictionary<string, object?>> _rows;

        public FakeWmiRunner(IEnumerable<IReadOnlyDictionary<string, object?>> rows)
        {
            _rows = rows;
        }

        public async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> QueryAsync(
            string scope,
            string wqlQuery,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var row in _rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return row;
            }
        }
    }
}
