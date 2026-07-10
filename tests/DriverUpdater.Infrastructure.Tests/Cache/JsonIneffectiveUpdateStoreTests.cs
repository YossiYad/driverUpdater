using DriverUpdater.Infrastructure.Cache;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriverUpdater.Infrastructure.Tests.Cache;

public class JsonIneffectiveUpdateStoreTests : IDisposable
{
    private readonly string _path;

    public JsonIneffectiveUpdateStoreTests()
    {
        _path = Path.Combine(Path.GetTempPath(), "DriverUpdaterIneffTests", Guid.NewGuid().ToString("N"), "ineffective-updates.json");
    }

    public void Dispose()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch
        {
        }
    }

    [Fact]
    public async Task LoadAsync_returns_empty_when_file_missing()
    {
        var store = NewStore();

        var records = await store.LoadAsync();

        records.Should().BeEmpty();
    }

    [Fact]
    public async Task RecordAsync_then_LoadAsync_round_trips_a_record()
    {
        var store = NewStore();

        await store.RecordAsync("SWD\\DEV\\VOICE", "10.0.26100.6710", "1.0.4.7057");
        var records = await store.LoadAsync();

        records.Should().ContainSingle();
        records[0].DeviceId.Should().Be("SWD\\DEV\\VOICE");
        records[0].TargetVersion.Should().Be("10.0.26100.6710");
        records[0].InstalledVersionAtAttempt.Should().Be("1.0.4.7057");
        records[0].AttemptCount.Should().Be(1);
    }

    [Fact]
    public async Task RecordAsync_increments_count_and_updates_installed_version_for_same_key()
    {
        var store = NewStore();

        await store.RecordAsync("dev", "2.0.0.0", "1.0.0.0");
        await store.RecordAsync("dev", "2.0.0.0", "1.0.0.0");

        var records = await store.LoadAsync();
        records.Should().ContainSingle();
        records[0].AttemptCount.Should().Be(2);
    }

    [Fact]
    public async Task RecordAsync_keeps_separate_entries_for_different_targets()
    {
        var store = NewStore();

        await store.RecordAsync("dev", "2.0.0.0", "1.0.0.0");
        await store.RecordAsync("dev", "3.0.0.0", "1.0.0.0");

        var records = await store.LoadAsync();
        records.Should().HaveCount(2);
    }

    [Fact]
    public async Task RecordAsync_ignores_blank_device_or_target()
    {
        var store = NewStore();

        await store.RecordAsync("", "2.0.0.0", "1.0.0.0");
        await store.RecordAsync("dev", "  ", "1.0.0.0");

        (await store.LoadAsync()).Should().BeEmpty();
    }

    private JsonIneffectiveUpdateStore NewStore() =>
        new(NullLogger<JsonIneffectiveUpdateStore>.Instance, _path);
}
