using System.Runtime.CompilerServices;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using DriverUpdater.Core.Options;
using DriverUpdater.Services.Scanning;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DriverUpdater.Services.Tests.Scanning;

public class ScheduledScanRunnerTests
{
    [Fact]
    public async Task RunAsync_scan_only_matches_candidates_and_saves_cache_without_installing()
    {
        var driverA = NewDriver("Intel Display", "PCI\\VEN_8086&DEV_4682", new Version(1, 0, 0, 0));
        var driverB = NewDriver("Realtek Audio", "PCI\\VEN_10EC&DEV_8168", new Version(2, 0, 0, 0));
        var candidate = NewCandidate("PCI\\VEN_8086&DEV_4682", new Version(2, 0, 0, 0));
        var cache = new StubDriverCacheStore();

        var runner = NewRunner(
            new[] { driverA, driverB },
            new[] { new FakeUpdateSource(UpdateSource.WindowsUpdate, candidate) },
            new ThrowingInstallPipeline(),
            cache);

        await runner.RunAsync(installUpdates: false);

        cache.Saved.Should().ContainSingle();
        var entries = cache.Saved[0].Entries;
        entries.Should().ContainSingle(e => e.Driver.DeviceName == "Intel Display"
            && e.AvailableUpdate != null && e.Status == DriverStatus.Outdated);
        entries.Should().ContainSingle(e => e.Driver.DeviceName == "Realtek Audio" && e.AvailableUpdate == null);
    }

    [Fact]
    public async Task RunAsync_ignores_candidate_that_is_not_newer()
    {
        var driver = NewDriver("Intel Display", "PCI\\VEN_8086&DEV_4682", new Version(3, 0, 0, 0));
        var candidate = NewCandidate("PCI\\VEN_8086&DEV_4682", new Version(2, 0, 0, 0));
        var cache = new StubDriverCacheStore();

        var runner = NewRunner(
            new[] { driver },
            new[] { new FakeUpdateSource(UpdateSource.WindowsUpdate, candidate) },
            new ThrowingInstallPipeline(),
            cache);

        await runner.RunAsync(installUpdates: true);

        cache.Saved[0].Entries.Single().AvailableUpdate.Should().BeNull();
    }

    [Fact]
    public async Task RunAsync_matches_candidate_returned_for_a_secondary_hardware_id()
    {
        const string primaryId = "PCI\\VEN_8086&DEV_4682";
        const string secondaryId = "PCI\\VEN_8086&DEV_4682&SUBSYS_1234";
        var driver = NewDriver("Intel Display", primaryId, new Version(1, 0, 0, 0)) with
        {
            HardwareIds = new[] { primaryId, secondaryId }
        };
        var cache = new StubDriverCacheStore();
        var runner = NewRunner(
            new[] { driver },
            new[] { new FakeUpdateSource(UpdateSource.WindowsUpdate,
                NewCandidate(secondaryId, new Version(2, 0, 0, 0))) },
            new ThrowingInstallPipeline(),
            cache);

        await runner.RunAsync(installUpdates: false);

        cache.Saved[0].Entries.Single().AvailableUpdate.Should().NotBeNull();
    }

    [Fact]
    public async Task RunAsync_installs_confirmed_updates_when_requested()
    {
        var driver = NewDriver("Intel Display", "PCI\\VEN_8086&DEV_4682", new Version(1, 0, 0, 0));
        var candidate = NewCandidate("PCI\\VEN_8086&DEV_4682", new Version(2, 0, 0, 0));
        var pipeline = new RecordingInstallPipeline(UpdateStatus.Succeeded);
        var cache = new StubDriverCacheStore();

        var runner = NewRunner(
            new[] { driver },
            new[] { new FakeUpdateSource(UpdateSource.WindowsUpdate, candidate) },
            pipeline,
            cache);

        await runner.RunAsync(installUpdates: true);

        pipeline.Operations.Should().ContainSingle()
            .Which.Candidate.SourceUpdateId.Should().Be(candidate.SourceUpdateId);
        pipeline.Options.Should().ContainSingle();
        pipeline.Options[0].CreateRestorePoint.Should().BeTrue();
        pipeline.Options[0].BackupCurrentDriver.Should().BeTrue();

        var entry = cache.Saved[0].Entries.Single();
        entry.Status.Should().Be(DriverStatus.UpToDate);
        entry.AvailableUpdate.Should().BeNull("a succeeded install clears the candidate");
    }

    [Fact]
    public async Task RunAsync_runs_a_shared_installer_once_and_fans_the_result_to_every_row()
    {
        const string sharedId = "vendor-installer:amd-chipset:8.05";
        var driverA = NewDriver("AMD Tools", "ROOT\\SYSTEM\\0001", new Version(1, 0, 0, 0));
        var driverB = NewDriver("AMD Defender", "ROOT\\SYSTEM\\0002", new Version(1, 0, 0, 0));
        var driverC = NewDriver("AMD Processor", "ROOT\\SYSTEM\\0003", new Version(1, 0, 0, 0));
        var candA = NewCandidate("ROOT\\SYSTEM\\0001", new Version(2, 0, 0, 0), UpdateInstallKind.VendorInstaller) with { SourceUpdateId = sharedId };
        var candB = NewCandidate("ROOT\\SYSTEM\\0002", new Version(2, 0, 0, 0), UpdateInstallKind.VendorInstaller) with { SourceUpdateId = sharedId };
        var candC = NewCandidate("ROOT\\SYSTEM\\0003", new Version(2, 0, 0, 0), UpdateInstallKind.VendorInstaller) with { SourceUpdateId = sharedId };
        var pipeline = new RecordingInstallPipeline(UpdateStatus.Succeeded);
        var cache = new StubDriverCacheStore();

        var runner = NewRunner(
            new[] { driverA, driverB, driverC },
            new[] { new FakeUpdateSource(UpdateSource.Oem, candA, candB, candC) },
            pipeline,
            cache);

        await runner.RunAsync(installUpdates: true);

        pipeline.Operations.Should().ContainSingle("the shared installer must run only once");
        cache.Saved[0].Entries.Should().OnlyContain(e => e.Status == DriverStatus.UpToDate && e.AvailableUpdate == null);
    }

    [Fact]
    public async Task RunAsync_keeps_candidate_when_install_fails()
    {
        var driver = NewDriver("Intel Display", "PCI\\VEN_8086&DEV_4682", new Version(1, 0, 0, 0));
        var candidate = NewCandidate("PCI\\VEN_8086&DEV_4682", new Version(2, 0, 0, 0));
        var pipeline = new RecordingInstallPipeline(UpdateStatus.Failed);
        var cache = new StubDriverCacheStore();

        var runner = NewRunner(
            new[] { driver },
            new[] { new FakeUpdateSource(UpdateSource.WindowsUpdate, candidate) },
            pipeline,
            cache);

        await runner.RunAsync(installUpdates: true);

        var entry = cache.Saved[0].Entries.Single();
        entry.Status.Should().Be(DriverStatus.Error);
        entry.AvailableUpdate.Should().NotBeNull("a failed install leaves the candidate for a later retry");
    }

    [Fact]
    public async Task RunAsync_does_not_install_vendor_page_advisories()
    {
        var driver = NewDriver("NVIDIA Display", "PCI\\VEN_10DE&DEV_0001", new Version(1, 0, 0, 0));
        var advisory = NewCandidate(
            "PCI\\VEN_10DE&DEV_0001",
            new Version(2026, 5, 28, 0),
            UpdateInstallKind.VendorPage,
            UpdateConfidence.Advisory);
        var pipeline = new ThrowingInstallPipeline();
        var cache = new StubDriverCacheStore();

        var runner = NewRunner(
            new[] { driver },
            new[] { new FakeUpdateSource(UpdateSource.Oem, advisory) },
            pipeline,
            cache);

        await runner.RunAsync(installUpdates: true);

        cache.Saved[0].Entries.Single().AvailableUpdate.Should().NotBeNull("vendor-page advisories survive but are never auto-installed");
    }

    [Fact]
    public async Task RunAsync_skips_sources_disabled_in_settings()
    {
        var driver = NewDriver("AMD Display", "PCI\\VEN_1002&DEV_747E", new Version(1, 0, 0, 0));
        var candidate = NewCandidate("PCI\\VEN_1002&DEV_747E", new Version(2, 0, 0, 0));
        var source = new FakeUpdateSource(UpdateSource.Oem, candidate);
        var cache = new StubDriverCacheStore();

        var runner = NewRunner(
            new[] { driver },
            new[] { source },
            new ThrowingInstallPipeline(),
            cache,
            new UpdaterSettings { OemSourcesEnabled = false });

        await runner.RunAsync(installUpdates: true);

        source.SearchInvocations.Should().Be(0);
        cache.Saved[0].Entries.Single().AvailableUpdate.Should().BeNull();
    }

    private static ScheduledScanRunner NewRunner(
        IReadOnlyList<DriverInfo> drivers,
        IReadOnlyList<IUpdateSource> sources,
        IInstallPipeline pipeline,
        IDriverCacheStore cache,
        UpdaterSettings? settings = null) =>
        new(
            new FakeScanService(drivers),
            sources,
            pipeline,
            cache,
            new StubOptionsMonitor<UpdaterSettings>(settings ?? new UpdaterSettings()),
            NullLogger<ScheduledScanRunner>.Instance);

    private static DriverInfo NewDriver(string name, string hardwareId, Version version) => new(
        DeviceId: $"ID\\{name}",
        HardwareId: hardwareId,
        DeviceName: name,
        Category: DriverCategory.Display,
        Provider: "TestProvider",
        Manufacturer: "TestMaker",
        CurrentVersion: version,
        CurrentDate: new DateOnly(2024, 1, 1),
        InfName: "oem.inf",
        InfPath: null,
        IsSigned: true,
        DeviceClass: "Display");

    private static UpdateCandidate NewCandidate(
        string hardwareId,
        Version newVersion,
        UpdateInstallKind installKind = UpdateInstallKind.WindowsUpdate,
        UpdateConfidence confidence = UpdateConfidence.Confirmed) => new(
        ForHardwareId: hardwareId,
        Source: UpdateSource.WindowsUpdate,
        NewVersion: newVersion,
        NewDate: new DateOnly(2026, 1, 1),
        DownloadUrl: new Uri("https://example.com/x.cab"),
        SizeBytes: 1024,
        KbArticle: null,
        IsSuperseded: false,
        SourceUpdateId: Guid.NewGuid().ToString(),
        SupersededIds: Array.Empty<string>(),
        InstallKind: installKind,
        Confidence: confidence);

    private sealed class FakeScanService : IDriverScanService
    {
        private readonly IReadOnlyList<DriverInfo> _drivers;
        public FakeScanService(IReadOnlyList<DriverInfo> drivers) => _drivers = drivers;

        public async IAsyncEnumerable<DriverInfo> ScanAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var driver in _drivers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return driver;
            }
        }
    }

    private sealed class FakeUpdateSource : IUpdateSource
    {
        private readonly UpdateCandidate[] _candidates;
        public FakeUpdateSource(UpdateSource kind, params UpdateCandidate[] candidates)
        {
            Kind = kind;
            _candidates = candidates;
        }

        public int SearchInvocations { get; private set; }
        public UpdateSource Kind { get; }
        public string DisplayName => $"Fake {Kind}";

        public async IAsyncEnumerable<UpdateCandidate> SearchAsync(
            IReadOnlyCollection<DriverInfo> drivers,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            SearchInvocations++;
            foreach (var candidate in _candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return candidate;
            }
        }
    }

    private sealed class RecordingInstallPipeline : IInstallPipeline
    {
        private readonly UpdateStatus _outcome;
        public RecordingInstallPipeline(UpdateStatus outcome) => _outcome = outcome;

        public List<UpdateOperation> Operations { get; } = new();
        public List<InstallOptions> Options { get; } = new();

        public Task<UpdateOperation> ExecuteAsync(
            UpdateOperation operation,
            InstallOptions options,
            IProgress<UpdateOperation>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Operations.Add(operation);
            Options.Add(options);
            var finished = operation with
            {
                Status = _outcome,
                ErrorMessage = _outcome == UpdateStatus.Failed ? "boom" : null,
                CompletedAt = DateTimeOffset.UtcNow
            };
            progress?.Report(finished);
            return Task.FromResult(finished);
        }
    }

    private sealed class ThrowingInstallPipeline : IInstallPipeline
    {
        public Task<UpdateOperation> ExecuteAsync(
            UpdateOperation operation,
            InstallOptions options,
            IProgress<UpdateOperation>? progress = null,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("install should not be called");
    }

    private sealed class StubDriverCacheStore : IDriverCacheStore
    {
        public event EventHandler? Cleared;

        public List<DriverCacheSnapshot> Saved { get; } = new();

        public Task<DriverCacheSnapshot?> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<DriverCacheSnapshot?>(null);

        public Task SaveAsync(DriverCacheSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            Saved.Add(snapshot);
            return Task.CompletedTask;
        }

        public Task<int> ClearAsync(CancellationToken cancellationToken = default)
        {
            Cleared?.Invoke(this, EventArgs.Empty);
            return Task.FromResult(0);
        }
    }

    private sealed class StubOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public StubOptionsMonitor(T value) => CurrentValue = value;
        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
