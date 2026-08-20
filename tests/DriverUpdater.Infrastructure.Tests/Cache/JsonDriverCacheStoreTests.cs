using DriverUpdater.Core.Models;
using DriverUpdater.Core.Options;
using DriverUpdater.Infrastructure.Cache;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

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

    [Fact]
    public async Task ClearAsync_deletes_the_cache_and_reports_removed_updates()
    {
        var store = NewStore();
        var driver = NewDriver("Display", "TEST_HWID", DriverCategory.Display);
        var candidate = new UpdateCandidate(
            ForHardwareId: driver.HardwareId,
            Source: UpdateSource.MicrosoftCatalog,
            NewVersion: new Version(2, 0, 0, 0),
            NewDate: new DateOnly(2026, 1, 1),
            DownloadUrl: new Uri("https://download.example.com/update.cab"),
            SizeBytes: 1024,
            KbArticle: null,
            IsSuperseded: false,
            SourceUpdateId: "new-update",
            SupersededIds: Array.Empty<string>());
        await store.SaveAsync(new DriverCacheSnapshot(
            DateTimeOffset.UtcNow,
            new[] { new CachedDriverEntry(driver, DriverStatus.Outdated, candidate) }));
        var eventRaised = false;
        store.Cleared += (_, _) => eventRaised = true;

        var removed = await store.ClearAsync();

        removed.Should().Be(1);
        eventRaised.Should().BeTrue();
        File.Exists(_path).Should().BeFalse();
        (await store.LoadAsync()).Should().BeNull();
    }

    [Fact]
    public async Task Concurrent_stores_serialize_writes_and_leave_valid_json()
    {
        var stores = new[] { NewStore(), NewStore() };
        var driver = NewDriver("Display", "TEST_HWID", DriverCategory.Display);
        var writes = Enumerable.Range(0, 20).Select(index => stores[index % stores.Length].SaveAsync(
            new DriverCacheSnapshot(
                DateTimeOffset.UtcNow.AddSeconds(index),
                new[] { new CachedDriverEntry(driver, DriverStatus.UpToDate, null) })));

        await Task.WhenAll(writes);

        var loaded = await NewStore().LoadAsync();
        loaded.Should().NotBeNull();
        loaded!.Entries.Should().ContainSingle();
        Directory.GetFiles(Path.GetDirectoryName(_path)!, "*.tmp").Should().BeEmpty();
    }

    [Theory]
    [InlineData("DESKTOP-AB12", "driver-cache.DESKTOP-AB12.json")]
    [InlineData("PC:with*bad|chars", "driver-cache.PC_with_bad_chars.json")]
    [InlineData("", "driver-cache.default.json")]
    public void BuildMachineCacheFileName_sanitizes_machine_name(string machine, string expected)
    {
        JsonDriverCacheStore.BuildMachineCacheFileName(machine).Should().Be(expected);
    }

    [Fact]
    public void Default_cache_path_is_per_machine()
    {
        var store = new JsonDriverCacheStore(NullLogger<JsonDriverCacheStore>.Instance);

        Path.GetFileName(store.CachePath).Should().Be(
            JsonDriverCacheStore.BuildMachineCacheFileName(Environment.MachineName));
    }

    [Fact]
    public async Task LoadAsync_returns_a_snapshot_that_is_still_inside_the_retention_window()
    {
        var store = NewStore(new ScanCacheSettings { ExpirationEnabled = true, RetentionHours = 24 });
        await store.SaveAsync(NewSnapshot(DateTimeOffset.UtcNow.AddHours(-23)));

        var snapshot = await store.LoadAsync();

        snapshot.Should().NotBeNull();
        File.Exists(_path).Should().BeTrue();
    }

    [Fact]
    public async Task LoadAsync_deletes_and_ignores_a_snapshot_older_than_the_retention_window()
    {
        var store = NewStore(new ScanCacheSettings { ExpirationEnabled = true, RetentionHours = 24 });
        await store.SaveAsync(NewSnapshot(DateTimeOffset.UtcNow.AddHours(-25)));

        var snapshot = await store.LoadAsync();

        snapshot.Should().BeNull();
        File.Exists(_path).Should().BeFalse();
    }

    // Expiry is not a user-requested clear: a scan running at that moment would throw away the
    // results it just produced if the Cleared handler fired.
    [Fact]
    public async Task LoadAsync_does_not_raise_Cleared_when_a_snapshot_expires()
    {
        var store = NewStore(new ScanCacheSettings { ExpirationEnabled = true, RetentionHours = 1 });
        await store.SaveAsync(NewSnapshot(DateTimeOffset.UtcNow.AddHours(-5)));
        var cleared = 0;
        store.Cleared += (_, _) => cleared++;

        await store.LoadAsync();

        cleared.Should().Be(0);
    }

    [Fact]
    public async Task LoadAsync_keeps_an_old_snapshot_when_expiration_is_turned_off()
    {
        var store = NewStore(new ScanCacheSettings { ExpirationEnabled = false, RetentionHours = 1 });
        await store.SaveAsync(NewSnapshot(DateTimeOffset.UtcNow.AddDays(-90)));

        var snapshot = await store.LoadAsync();

        snapshot.Should().NotBeNull();
        File.Exists(_path).Should().BeTrue();
    }

    // Without settings (the default constructor used by tests and older call sites) nothing
    // expires, so behaviour matches the app before retention existed.
    [Fact]
    public async Task LoadAsync_keeps_an_old_snapshot_when_no_retention_settings_are_supplied()
    {
        var store = NewStore();
        await store.SaveAsync(NewSnapshot(DateTimeOffset.UtcNow.AddDays(-90)));

        var snapshot = await store.LoadAsync();

        snapshot.Should().NotBeNull();
    }

    [Fact]
    public async Task ClearAsync_still_counts_the_updates_in_an_expired_snapshot()
    {
        var store = NewStore(new ScanCacheSettings { ExpirationEnabled = true, RetentionHours = 1 });
        var driver = NewDriver("Realtek Audio", "PCI\\VEN_10EC&DEV_8168", DriverCategory.Audio);
        var candidate = NewCandidate(driver);
        await store.SaveAsync(new DriverCacheSnapshot(
            DateTimeOffset.UtcNow.AddDays(-3),
            new[] { new CachedDriverEntry(driver, DriverStatus.Outdated, candidate) }));

        var removed = await store.ClearAsync();

        removed.Should().Be(1);
        File.Exists(_path).Should().BeFalse();
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(24, 24)]
    [InlineData(99999, 8760)]
    public void ResolveRetentionWindow_clamps_the_configured_hours(int configured, int expectedHours)
    {
        var window = JsonDriverCacheStore.ResolveRetentionWindow(
            new ScanCacheSettings { ExpirationEnabled = true, RetentionHours = configured });

        window.Should().Be(TimeSpan.FromHours(expectedHours));
    }

    [Fact]
    public void ResolveRetentionWindow_is_null_when_expiration_is_disabled()
    {
        JsonDriverCacheStore.ResolveRetentionWindow(
            new ScanCacheSettings { ExpirationEnabled = false }).Should().BeNull();
        JsonDriverCacheStore.ResolveRetentionWindow(null).Should().BeNull();
    }

    private JsonDriverCacheStore NewStore() =>
        new(NullLogger<JsonDriverCacheStore>.Instance, _path);

    private JsonDriverCacheStore NewStore(ScanCacheSettings settings) =>
        new(NullLogger<JsonDriverCacheStore>.Instance, _path, new ConstantOptionsMonitor<ScanCacheSettings>(settings));

    private DriverCacheSnapshot NewSnapshot(DateTimeOffset capturedAt) => new(
        capturedAt,
        new[]
        {
            new CachedDriverEntry(
                NewDriver("Realtek Audio", "PCI\\VEN_10EC&DEV_8168", DriverCategory.Audio),
                DriverStatus.UpToDate,
                null)
        });

    private static UpdateCandidate NewCandidate(DriverInfo driver) => new(
        ForHardwareId: driver.HardwareId,
        Source: UpdateSource.Oem,
        NewVersion: new Version(2, 0, 0, 0),
        NewDate: new DateOnly(2026, 5, 14),
        DownloadUrl: new Uri("https://example.test/driver.exe"),
        SizeBytes: 1024,
        KbArticle: null,
        IsSuperseded: false,
        SourceUpdateId: "test:candidate",
        SupersededIds: Array.Empty<string>());

    private sealed class ConstantOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public ConstantOptionsMonitor(T value) { CurrentValue = value; }
        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string> listener) => null;
    }

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
