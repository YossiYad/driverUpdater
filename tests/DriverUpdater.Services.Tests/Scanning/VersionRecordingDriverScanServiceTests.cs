using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using DriverUpdater.Services.Scanning;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriverUpdater.Services.Tests.Scanning;

public class VersionRecordingDriverScanServiceTests
{
    private static DriverInfo NewDriver(string deviceId) => new(
        DeviceId: deviceId,
        HardwareId: $"HW\\{deviceId}",
        DeviceName: $"Device {deviceId}",
        Category: DriverCategory.Display,
        Provider: "Vendor",
        Manufacturer: "Vendor",
        CurrentVersion: new Version(1, 0),
        CurrentDate: null,
        InfName: "oem1.inf",
        InfPath: null,
        IsSigned: true,
        DeviceClass: "Display");

    [Fact]
    public async Task Scan_streams_all_drivers_and_records_the_snapshot_once()
    {
        var store = new RecordingStore();
        var service = new VersionRecordingDriverScanService(
            new FakeScan(NewDriver("a"), NewDriver("b")),
            store,
            NullLogger<VersionRecordingDriverScanService>.Instance);

        var streamed = new List<DriverInfo>();
        await foreach (var driver in service.ScanAsync())
        {
            streamed.Add(driver);
        }

        streamed.Should().HaveCount(2);
        store.RecordedSnapshots.Should().ContainSingle();
        store.RecordedSnapshots[0].Should().HaveCount(2);
    }

    [Fact]
    public async Task Scan_still_succeeds_when_recording_fails()
    {
        var store = new RecordingStore { ThrowOnRecord = true };
        var service = new VersionRecordingDriverScanService(
            new FakeScan(NewDriver("a")),
            store,
            NullLogger<VersionRecordingDriverScanService>.Instance);

        var streamed = new List<DriverInfo>();
        var act = async () =>
        {
            await foreach (var driver in service.ScanAsync())
            {
                streamed.Add(driver);
            }
        };

        await act.Should().NotThrowAsync();
        streamed.Should().ContainSingle();
    }

    private sealed class FakeScan : IDriverScanService
    {
        private readonly DriverInfo[] _drivers;
        public FakeScan(params DriverInfo[] drivers) => _drivers = drivers;

        public async IAsyncEnumerable<DriverInfo> ScanAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var driver in _drivers)
            {
                await Task.Yield();
                yield return driver;
            }
        }
    }

    private sealed class RecordingStore : IDriverVersionHistoryStore
    {
        public bool ThrowOnRecord { get; init; }

        public List<IReadOnlyList<DriverInfo>> RecordedSnapshots { get; } = new();

        public Task RecordScanAsync(IReadOnlyList<DriverInfo> drivers, CancellationToken cancellationToken = default)
        {
            if (ThrowOnRecord)
            {
                throw new IOException("disk full");
            }
            RecordedSnapshots.Add(drivers.ToArray());
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<DriverVersionRecord>> GetHistoryAsync(string deviceId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DriverVersionRecord>>(Array.Empty<DriverVersionRecord>());
    }
}
