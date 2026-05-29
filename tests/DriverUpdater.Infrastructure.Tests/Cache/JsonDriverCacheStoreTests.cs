using DriverUpdater.Core.Models;
using DriverUpdater.Infrastructure.Cache;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriverUpdater.Infrastructure.Tests.Cache;

public class JsonDriverCacheStoreTests : IDisposable
{
    private readonly string _path;

    public JsonDriverCacheStoreTests()
    {
        _path = Path.Combine(Path.GetTempPath(), "DriverUpdaterCacheTests", Guid.NewGuid().ToString("N"), "driver-cache.json");
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
    public async Task LoadAsync_returns_null_when_file_missing()
    {
        var store = NewStore();

        var snapshot = await store.LoadAsync();

        snapshot.Should().BeNull();
    }

    [Fact]
    public async Task LoadAsync_returns_null_on_corrupt_json_without_throwing()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        await File.WriteAllTextAsync(_path, "{ this is not valid json ]");
        var store = NewStore();

        var snapshot = await store.LoadAsync();

        snapshot.Should().BeNull();
    }

    [Fact]
    public async Task SaveAsync_then_LoadAsync_round_trips_entries_with_and_without_candidate()
    {
        var store = NewStore();
        var captured = new DateTimeOffset(2026, 5, 29, 22, 15, 0, TimeSpan.Zero);
        var outdated = NewDriver("AMD Radeon RX 7700 XT", "PCI\\VEN_1002&DEV_747E", DriverCategory.Display);
        var upToDate = NewDriver("Realtek Audio", "PCI\\VEN_10EC&DEV_8168", DriverCategory.Audio);
        var candidate = new UpdateCandidate(
            ForHardwareId: outdated.HardwareId,
            Source: UpdateSource.Oem,
            NewVersion: new Version(2026, 5, 14, 0),
            NewDate: new DateOnly(2026, 5, 14),
            DownloadUrl: new Uri("https://drivers.amd.com/x.exe"),
            SizeBytes: 857845352,
            KbArticle: null,
            IsSuperseded: false,
            SourceUpdateId: "vendor-installer:nullsoft:amd-radeon:26.5.2",
            SupersededIds: new[] { "old-1", "old-2" },
            InstallKind: UpdateInstallKind.VendorInstaller,
            Confidence: UpdateConfidence.Confirmed);

        var snapshot = new DriverCacheSnapshot(captured, new[]
        {
            new CachedDriverEntry(outdated, DriverStatus.Outdated, candidate),
            new CachedDriverEntry(upToDate, DriverStatus.UpToDate, null)
        });

        await store.SaveAsync(snapshot);
        var loaded = await store.LoadAsync();

        loaded.Should().NotBeNull();
        loaded!.CapturedAt.Should().Be(captured);
        loaded.Entries.Should().HaveCount(2);

        var first = loaded.Entries[0];
        first.Driver.DeviceName.Should().Be("AMD Radeon RX 7700 XT");
        first.Driver.HardwareId.Should().Be("PCI\\VEN_1002&DEV_747E");
        first.Driver.Category.Should().Be(DriverCategory.Display);
        first.Status.Should().Be(DriverStatus.Outdated);
        first.AvailableUpdate.Should().NotBeNull();
        first.AvailableUpdate!.NewVersion.Should().Be(new Version(2026, 5, 14, 0));
        first.AvailableUpdate.NewDate.Should().Be(new DateOnly(2026, 5, 14));
        first.AvailableUpdate.DownloadUrl.AbsoluteUri.Should().Be("https://drivers.amd.com/x.exe");
        first.AvailableUpdate.SourceUpdateId.Should().Be("vendor-installer:nullsoft:amd-radeon:26.5.2");
        first.AvailableUpdate.SupersededIds.Should().BeEquivalentTo(new[] { "old-1", "old-2" });
        first.AvailableUpdate.InstallKind.Should().Be(UpdateInstallKind.VendorInstaller);

        var second = loaded.Entries[1];
        second.Driver.DeviceName.Should().Be("Realtek Audio");
        second.Status.Should().Be(DriverStatus.UpToDate);
        second.AvailableUpdate.Should().BeNull();
    }

    private JsonDriverCacheStore NewStore() =>
        new(NullLogger<JsonDriverCacheStore>.Instance, _path);

    private static DriverInfo NewDriver(string name, string hardwareId, DriverCategory category) => new(
        DeviceId: $"{hardwareId}\\3&abc&0",
        HardwareId: hardwareId,
        DeviceName: name,
        Category: category,
        Provider: "TestProvider",
        Manufacturer: "TestMaker",
        CurrentVersion: new Version(1, 0, 0, 0),
        CurrentDate: new DateOnly(2024, 1, 1),
        InfName: "oem.inf",
        InfPath: null,
        IsSigned: true,
        DeviceClass: category.ToString());
}
