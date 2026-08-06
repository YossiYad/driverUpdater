using DriverUpdater.Core.Models;
using DriverUpdater.Infrastructure.Settings;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriverUpdater.Infrastructure.Tests.Settings;

public class JsonDriverUpdateExclusionStoreTests : IDisposable
{
    private readonly string _path;

    public JsonDriverUpdateExclusionStoreTests()
    {
        _path = Path.Combine(
            Path.GetTempPath(),
            "DriverUpdaterExclusionTests",
            Guid.NewGuid().ToString("N"),
            "excluded-drivers.json");
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
    public async Task LoadAsync_returns_an_empty_list_when_the_file_is_missing()
    {
        var exclusions = await NewStore().LoadAsync();

        exclusions.DeviceIds.Should().BeEmpty();
    }

    [Fact]
    public async Task SaveAsync_then_LoadAsync_round_trips_the_excluded_devices()
    {
        var store = NewStore();

        await store.SaveAsync(new DriverUpdateExclusions(new[] { "PCI\\A", "PCI\\B" }));
        var exclusions = await NewStore().LoadAsync();

        exclusions.DeviceIds.Should().BeEquivalentTo("PCI\\A", "PCI\\B");
        exclusions.Contains("pci\\a").Should().BeTrue("device ids are matched case-insensitively");
        exclusions.Contains("PCI\\C").Should().BeFalse();
    }

    [Fact]
    public async Task SaveAsync_drops_blank_and_duplicate_device_ids()
    {
        var store = NewStore();

        await store.SaveAsync(new DriverUpdateExclusions(new[] { "PCI\\A", " ", "pci\\a", "  PCI\\B  " }));
        var exclusions = await store.LoadAsync();

        exclusions.DeviceIds.Should().BeEquivalentTo("PCI\\A", "PCI\\B");
    }

    [Fact]
    public async Task SaveAsync_replaces_an_existing_selection_instead_of_leaving_stale_devices()
    {
        var store = NewStore();
        await store.SaveAsync(new DriverUpdateExclusions(new[] { "PCI\\OLD" }));

        await store.SaveAsync(new DriverUpdateExclusions(new[] { "PCI\\NEW" }));
        var exclusions = await store.LoadAsync();

        exclusions.DeviceIds.Should().Equal("PCI\\NEW");
        exclusions.Contains("PCI\\OLD").Should().BeFalse();
    }

    [Fact]
    public async Task LoadAsync_releases_its_lock_for_the_next_read()
    {
        var store = NewStore();

        var first = await store.LoadAsync();
        var second = await store.LoadAsync().WaitAsync(TimeSpan.FromSeconds(2));

        first.DeviceIds.Should().BeEmpty();
        second.DeviceIds.Should().BeEmpty();
    }

    [Fact]
    public async Task LoadAsync_rejects_a_corrupt_file_instead_of_silently_enabling_updates()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        await File.WriteAllTextAsync(_path, "{ not json");

        Func<Task> act = async () => await NewStore().LoadAsync();

        await act.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*excluded-drivers.json*");
    }

    private JsonDriverUpdateExclusionStore NewStore() =>
        new(NullLogger<JsonDriverUpdateExclusionStore>.Instance, _path);
}
