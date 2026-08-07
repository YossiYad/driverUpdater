using DriverUpdater.Core.Models;
using DriverUpdater.Infrastructure.Settings;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriverUpdater.Infrastructure.Tests.Settings;

public class JsonDriverVersionHistoryStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"driver-version-history-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }

    private JsonDriverVersionHistoryStore NewStore(TimeProvider? clock = null) =>
        new(NullLogger<JsonDriverVersionHistoryStore>.Instance, _path, clock);

    private static DriverInfo NewDriver(string deviceId, string version, string? inf = "oem42.inf") => new(
        DeviceId: deviceId,
        HardwareId: $"HW\\{deviceId}",
        DeviceName: $"Device {deviceId}",
        Category: DriverCategory.Display,
        Provider: "Vendor",
        Manufacturer: "Vendor",
        CurrentVersion: Version.Parse(version),
        CurrentDate: new DateOnly(2025, 6, 1),
        InfName: inf,
        InfPath: null,
        IsSigned: true,
        DeviceClass: "Display");

    [Fact]
    public async Task RecordScan_persists_one_record_per_device_version()
    {
        var store = NewStore();

        await store.RecordScanAsync(new[]
        {
            NewDriver("dev-a", "1.0.0.0"),
            NewDriver("dev-b", "5.5.0.0")
        });

        var historyA = await store.GetHistoryAsync("dev-a");
        historyA.Should().ContainSingle();
        historyA[0].Version.Should().Be("1.0.0.0");
        historyA[0].InfName.Should().Be("oem42.inf");
    }

    [Fact]
    public async Task RecordScan_accumulates_versions_across_scans()
    {
        var store = NewStore();

        await store.RecordScanAsync(new[] { NewDriver("dev-a", "1.0.0.0", "oem10.inf") });
        await store.RecordScanAsync(new[] { NewDriver("dev-a", "2.0.0.0", "oem11.inf") });

        var history = await store.GetHistoryAsync("dev-a");
        history.Should().HaveCount(2);
        history.Select(r => r.Version).Should().BeEquivalentTo("1.0.0.0", "2.0.0.0");
    }

    [Fact]
    public async Task RecordScan_updates_last_seen_instead_of_duplicating()
    {
        var store = NewStore();
        var driver = NewDriver("dev-a", "1.0.0.0");

        await store.RecordScanAsync(new[] { driver });
        await store.RecordScanAsync(new[] { driver });

        var history = await store.GetHistoryAsync("dev-a");
        history.Should().ContainSingle();
        history[0].LastSeenAt.Should().BeOnOrAfter(history[0].FirstSeenAt);
    }

    [Fact]
    public async Task RecordScan_skips_drivers_without_a_version()
    {
        var store = NewStore();
        var noVersion = NewDriver("dev-a", "1.0.0.0") with { CurrentVersion = null };

        await store.RecordScanAsync(new[] { noVersion });

        var history = await store.GetHistoryAsync("dev-a");
        history.Should().BeEmpty();
    }

    [Fact]
    public async Task RecordScan_caps_versions_per_device()
    {
        var store = NewStore();
        for (var i = 0; i < JsonDriverVersionHistoryStore.MaxVersionsPerDevice + 5; i++)
        {
            await store.RecordScanAsync(new[] { NewDriver("dev-a", $"1.0.0.{i}") });
        }

        var history = await store.GetHistoryAsync("dev-a");
        history.Should().HaveCount(JsonDriverVersionHistoryStore.MaxVersionsPerDevice);
    }

    [Fact]
    public async Task GetHistory_survives_a_corrupt_file()
    {
        await File.WriteAllTextAsync(_path, "{ not json ]");
        var store = NewStore();

        var history = await store.GetHistoryAsync("dev-a");

        history.Should().BeEmpty();
    }
}
