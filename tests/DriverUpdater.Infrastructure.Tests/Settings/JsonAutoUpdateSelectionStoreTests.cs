using DriverUpdater.Core.Models;
using DriverUpdater.Infrastructure.Settings;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriverUpdater.Infrastructure.Tests.Settings;

public class JsonAutoUpdateSelectionStoreTests : IDisposable
{
    private readonly string _path;

    public JsonAutoUpdateSelectionStoreTests()
    {
        _path = Path.Combine(
            Path.GetTempPath(),
            "DriverUpdaterAutoUpdateSelectionTests",
            Guid.NewGuid().ToString("N"),
            "auto-update-selection.json");
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
    public async Task LoadAsync_returns_an_empty_selection_when_the_file_is_missing()
    {
        var selection = await NewStore().LoadAsync();

        selection.DeviceIds.Should().BeEmpty();
    }

    [Fact]
    public async Task SaveAsync_then_LoadAsync_round_trips_the_selected_devices()
    {
        var store = NewStore();

        await store.SaveAsync(new AutoUpdateSelection(new[] { "PCI\\A", "PCI\\B" }));
        var selection = await NewStore().LoadAsync();

        selection.DeviceIds.Should().BeEquivalentTo("PCI\\A", "PCI\\B");
        selection.Contains("pci\\a").Should().BeTrue("device ids are matched case-insensitively");
        selection.Contains("PCI\\C").Should().BeFalse();
    }

    [Fact]
    public async Task SaveAsync_drops_blank_and_duplicate_device_ids()
    {
        var store = NewStore();

        await store.SaveAsync(new AutoUpdateSelection(new[] { "PCI\\A", " ", "pci\\a", "  PCI\\B  " }));
        var selection = await store.LoadAsync();

        selection.DeviceIds.Should().BeEquivalentTo("PCI\\A", "PCI\\B");
    }

    [Fact]
    public async Task LoadAsync_returns_an_empty_selection_when_the_file_is_corrupt()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        await File.WriteAllTextAsync(_path, "{ not json");

        var selection = await NewStore().LoadAsync();

        selection.DeviceIds.Should().BeEmpty();
    }

    private JsonAutoUpdateSelectionStore NewStore() =>
        new(NullLogger<JsonAutoUpdateSelectionStore>.Instance, _path);
}
