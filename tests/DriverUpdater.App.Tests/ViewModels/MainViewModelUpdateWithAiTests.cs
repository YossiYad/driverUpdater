using System.Runtime.CompilerServices;
using DriverUpdater.App.Services;
using DriverUpdater.App.Tests.Stubs;
using DriverUpdater.App.ViewModels;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using DriverUpdater.Core.Options;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DriverUpdater.App.Tests.ViewModels;

public class MainViewModelUpdateWithAiTests
{
    [WpfFact]
    public async Task Installs_only_the_updates_the_ai_rated_within_the_risk_tolerance()
    {
        var safeDriver = NewDriver("Intel Wi-Fi", "PCI\\VEN_8086&DEV_2723", new Version(1, 0, 0, 0));
        var riskyDriver = NewDriver("AMD Radeon", "PCI\\VEN_1002&DEV_747E", new Version(1, 0, 0, 0));
        var safeCandidate = NewCandidate(safeDriver.HardwareId, new Version(2, 0, 0, 0), "wifi-update");
        var riskyCandidate = NewCandidate(riskyDriver.HardwareId, new Version(2, 0, 0, 0), "gpu-update");
        var verifier = new StubAiVerifier
        {
            Verdicts =
            {
                ["wifi-update"] = new AiVerdict(true, AiRiskLevel.Safe, "Routine update", "No reports", "2.0.0.0"),
                ["gpu-update"] = new AiVerdict(true, AiRiskLevel.HighRisk, "Black screens", "Many reports", "2.0.0.0")
            }
        };
        var pipeline = new RecordingInstallPipeline();

        var vm = NewVm(new[] { safeDriver, riskyDriver }, new[] { safeCandidate, riskyCandidate }, verifier, pipeline);
        await vm.UpdateWithAiCommand.ExecuteAsync(null);

        pipeline.InstalledDevices.Should().Equal("Intel Wi-Fi");
    }

    [WpfFact]
    public async Task Installs_caution_rated_updates_when_the_tolerance_allows_them()
    {
        var driver = NewDriver("Realtek Audio", "PCI\\VEN_10EC&DEV_8168", new Version(1, 0, 0, 0));
        var candidate = NewCandidate(driver.HardwareId, new Version(2, 0, 0, 0), "audio-update");
        var verifier = new StubAiVerifier
        {
            Verdicts = { ["audio-update"] = new AiVerdict(true, AiRiskLevel.Caution, "Minor issues", "Some reports", "2.0.0.0") }
        };
        var pipeline = new RecordingInstallPipeline();

        var vm = NewVm(
            new[] { driver },
            new[] { candidate },
            verifier,
            pipeline,
            AiAutoUpdateRiskTolerance.SafeAndCaution);
        await vm.UpdateWithAiCommand.ExecuteAsync(null);

        pipeline.InstalledDevices.Should().Equal("Realtek Audio");
    }

    [WpfFact]
    public async Task Installs_nothing_when_the_ai_returned_no_verdict()
    {
        var driver = NewDriver("Realtek Audio", "PCI\\VEN_10EC&DEV_8168", new Version(1, 0, 0, 0));
        var candidate = NewCandidate(driver.HardwareId, new Version(2, 0, 0, 0), "audio-update");
        var pipeline = new RecordingInstallPipeline();

        var vm = NewVm(new[] { driver }, new[] { candidate }, new StubAiVerifier(), pipeline);
        await vm.UpdateWithAiCommand.ExecuteAsync(null);

        pipeline.InstalledDevices.Should().BeEmpty();
        vm.StatusText.Should().Contain("did not endorse");
    }

    [WpfFact]
    public async Task Does_not_scan_when_no_ai_provider_is_configured()
    {
        var driver = NewDriver("Realtek Audio", "PCI\\VEN_10EC&DEV_8168", new Version(1, 0, 0, 0));
        var candidate = NewCandidate(driver.HardwareId, new Version(2, 0, 0, 0), "audio-update");
        var verifier = new StubAiVerifier { IsConfigured = false };
        var pipeline = new RecordingInstallPipeline();

        var vm = NewVm(new[] { driver }, new[] { candidate }, verifier, pipeline);
        await vm.UpdateWithAiCommand.ExecuteAsync(null);

        verifier.WasCalled.Should().BeFalse();
        vm.Drivers.Should().BeEmpty();
        pipeline.InstalledDevices.Should().BeEmpty();
        vm.StatusText.Should().Contain("Configure an AI provider");
    }

    private static MainViewModel NewVm(
        IEnumerable<DriverInfo> drivers,
        IEnumerable<UpdateCandidate> candidates,
        IAiVerifier aiVerifier,
        IInstallPipeline pipeline,
        AiAutoUpdateRiskTolerance tolerance = AiAutoUpdateRiskTolerance.SafeOnly) =>
        new(new FakeScanService(drivers),
            new[] { (IUpdateSource)new FakeUpdateSource(candidates) },
            new NullOemDetectionService(),
            pipeline,
            new AcceptingInstallConfirmation(),
            new NullHistoryWindowOpener(),
            new NullSettingsWindowOpener(),
            new NullLogsWindowOpener(),
            NullLogger<MainViewModel>.Instance,
            aiVerifier: aiVerifier,
            scheduleSettings: new StubOptionsMonitor<ScheduleSettings>(new ScheduleSettings { AiRiskTolerance = tolerance }));

    private static DriverInfo NewDriver(string name, string hardwareId, Version version) => new(
        DeviceId: $"ID\\{name}",
        HardwareId: hardwareId,
        DeviceName: name,
        Category: DriverCategory.Display,
        Provider: "Vendor",
        Manufacturer: "Vendor",
        CurrentVersion: version,
        CurrentDate: new DateOnly(2024, 1, 1),
        InfName: "oem.inf",
        InfPath: null,
        IsSigned: true,
        DeviceClass: "Display");

    private static UpdateCandidate NewCandidate(string hardwareId, Version newVersion, string sourceUpdateId) => new(
        ForHardwareId: hardwareId,
        Source: UpdateSource.WindowsUpdate,
        NewVersion: newVersion,
        NewDate: new DateOnly(2026, 1, 1),
        DownloadUrl: new Uri("https://example.com/x.cab"),
        SizeBytes: 1024,
        KbArticle: null,
        IsSuperseded: false,
        SourceUpdateId: sourceUpdateId,
        SupersededIds: Array.Empty<string>(),
        InstallKind: UpdateInstallKind.WindowsUpdate,
        Confidence: UpdateConfidence.Confirmed);

    private sealed class StubAiVerifier : IAiVerifier
    {
        public AiProvider Provider => AiProvider.Gemini;
        public bool IsConfigured { get; init; } = true;
        public bool IsTemporarilyUnavailable => false;
        public bool WasCalled { get; private set; }
        public Dictionary<string, AiVerdict> Verdicts { get; } = new();

        public Task<IReadOnlyDictionary<string, AiVerdict>> VerifyAsync(
            IReadOnlyList<AiVerificationRequest> requests,
            bool unattendedRun = false,
            CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            return Task.FromResult((IReadOnlyDictionary<string, AiVerdict>)Verdicts);
        }
    }

    private sealed class RecordingInstallPipeline : IInstallPipeline
    {
        public List<string> InstalledDevices { get; } = new();

        public Task<UpdateOperation> ExecuteAsync(
            UpdateOperation operation,
            InstallOptions options,
            IProgress<UpdateOperation>? progress = null,
            CancellationToken cancellationToken = default)
        {
            InstalledDevices.Add(operation.TargetSnapshot.DeviceName);
            return Task.FromResult(operation with
            {
                Status = UpdateStatus.Succeeded,
                ErrorMessage = null,
                CompletedAt = DateTimeOffset.UtcNow
            });
        }
    }

    private sealed class AcceptingInstallConfirmation : IInstallConfirmation
    {
        public InstallOptions? Confirm(UpdateOperation operation, bool dryRun) =>
            new(CreateRestorePoint: false, BackupCurrentDriver: false, DryRun: dryRun);
    }

    private sealed class StubOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public StubOptionsMonitor(T value) => CurrentValue = value;
        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class FakeScanService : IDriverScanService
    {
        private readonly IEnumerable<DriverInfo> _drivers;

        public FakeScanService(IEnumerable<DriverInfo> drivers) => _drivers = drivers;

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
        private readonly IEnumerable<UpdateCandidate> _candidates;

        public FakeUpdateSource(IEnumerable<UpdateCandidate> candidates) => _candidates = candidates;

        public UpdateSource Kind => UpdateSource.WindowsUpdate;
        public string DisplayName => "Fake";

        public async IAsyncEnumerable<UpdateCandidate> SearchAsync(
            IReadOnlyCollection<DriverInfo> drivers,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var candidate in _candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return candidate;
            }
        }
    }
}
