using System.Runtime.CompilerServices;
using DriverUpdater.App.Tests.Stubs;
using DriverUpdater.App.ViewModels;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriverUpdater.App.Tests.ViewModels;

public class MainViewModelAiTests
{
    [WpfFact]
    public async Task ScanAsync_suppresses_candidate_when_ai_says_not_genuinely_newer()
    {
        var driver = NewDriver("Realtek Audio", "PCI\\VEN_10EC&DEV_8168", new Version(1, 0, 0, 0));
        var candidate = NewCandidate("PCI\\VEN_10EC&DEV_8168", new Version(2, 0, 0, 0), "audio-update");
        var verifier = new StubAiVerifier(isConfigured: true)
        {
            Verdicts =
            {
                ["audio-update"] = new AiVerdict(
                    IsGenuinelyNewer: false, Risk: AiRiskLevel.Safe,
                    Summary: "Same driver, already installed", Rationale: "Identical version",
                    LatestKnownVersion: "1.0.0.0")
            }
        };

        var vm = NewVm(new[] { driver }, new[] { candidate }, verifier);
        await vm.ScanCommand.ExecuteAsync(null);

        verifier.WasCalled.Should().BeTrue();
        vm.Drivers[0].AvailableUpdate.Should().BeNull();
        vm.Drivers[0].Status.Should().Be(DriverStatus.UpToDate);
        vm.UpdatesFoundCount.Should().Be(0);
    }

    [WpfFact]
    public async Task ScanAsync_annotates_candidate_with_risk_when_ai_confirms_newer()
    {
        var driver = NewDriver("AMD Radeon", "PCI\\VEN_1002&DEV_747E", new Version(1, 0, 0, 0));
        var candidate = NewCandidate("PCI\\VEN_1002&DEV_747E", new Version(2, 0, 0, 0), "amd-update");
        var verdict = new AiVerdict(
            IsGenuinelyNewer: true, Risk: AiRiskLevel.HighRisk,
            Summary: "Reported black screens", Rationale: "Several user reports of crashes",
            LatestKnownVersion: "2.0.0.0");
        var verifier = new StubAiVerifier(isConfigured: true)
        {
            Verdicts = { ["amd-update"] = verdict }
        };

        var vm = NewVm(new[] { driver }, new[] { candidate }, verifier);
        await vm.ScanCommand.ExecuteAsync(null);

        vm.Drivers[0].AvailableUpdate.Should().NotBeNull();
        vm.Drivers[0].Status.Should().Be(DriverStatus.Outdated);
        vm.Drivers[0].AvailableUpdate!.AiVerification.Should().Be(verdict);
        vm.Drivers[0].AiRiskText.Should().Be("High risk");
        vm.Drivers[0].HasAiVerdict.Should().BeTrue();
        vm.UpdatesFoundCount.Should().Be(1);
    }

    [WpfFact]
    public async Task ScanAsync_does_not_call_ai_when_not_configured()
    {
        var driver = NewDriver("AMD Radeon", "PCI\\VEN_1002&DEV_747E", new Version(1, 0, 0, 0));
        var candidate = NewCandidate("PCI\\VEN_1002&DEV_747E", new Version(2, 0, 0, 0), "amd-update");
        var verifier = new StubAiVerifier(isConfigured: false);

        var vm = NewVm(new[] { driver }, new[] { candidate }, verifier);
        await vm.ScanCommand.ExecuteAsync(null);

        verifier.WasCalled.Should().BeFalse();
        vm.Drivers[0].AvailableUpdate.Should().NotBeNull();
        vm.Drivers[0].AvailableUpdate!.AiVerification.Should().BeNull();
        vm.UpdatesFoundCount.Should().Be(1);
    }

    [WpfFact]
    public async Task ScanAsync_leaves_results_unchanged_when_ai_throws()
    {
        var driver = NewDriver("AMD Radeon", "PCI\\VEN_1002&DEV_747E", new Version(1, 0, 0, 0));
        var candidate = NewCandidate("PCI\\VEN_1002&DEV_747E", new Version(2, 0, 0, 0), "amd-update");
        var verifier = new StubAiVerifier(isConfigured: true) { Throws = true };

        var vm = NewVm(new[] { driver }, new[] { candidate }, verifier);
        await vm.ScanCommand.ExecuteAsync(null);

        vm.Drivers[0].AvailableUpdate.Should().NotBeNull();
        vm.Drivers[0].Status.Should().Be(DriverStatus.Outdated);
        vm.UpdatesFoundCount.Should().Be(1);
    }

    [WpfFact]
    public async Task ScanAsync_sends_one_request_per_installer_and_applies_verdict_to_all_shared_rows()
    {
        // Three device rows that all share one installer (the AMD chipset case).
        var driverA = NewDriver("AMD Chipset A", "ROOT\\SYSTEM\\0001", new Version(1, 0, 0, 0));
        var driverB = NewDriver("AMD Chipset B", "ROOT\\SYSTEM\\0002", new Version(1, 0, 0, 0));
        var driverC = NewDriver("AMD Chipset C", "ROOT\\SYSTEM\\0003", new Version(1, 0, 0, 0));
        const string sharedId = "vendor-installer:amd-chipset:8.05";
        var candA = NewCandidate("ROOT\\SYSTEM\\0001", new Version(2, 0, 0, 0), sharedId, UpdateInstallKind.VendorInstaller);
        var candB = NewCandidate("ROOT\\SYSTEM\\0002", new Version(2, 0, 0, 0), sharedId, UpdateInstallKind.VendorInstaller);
        var candC = NewCandidate("ROOT\\SYSTEM\\0003", new Version(2, 0, 0, 0), sharedId, UpdateInstallKind.VendorInstaller);
        var verdict = new AiVerdict(true, AiRiskLevel.Caution, "ok", "fine", "2.0.0.0");
        var verifier = new StubAiVerifier(isConfigured: true) { Verdicts = { [sharedId] = verdict } };

        var vm = NewVm(new[] { driverA, driverB, driverC }, new[] { candA, candB, candC }, verifier);
        await vm.ScanCommand.ExecuteAsync(null);

        verifier.LastRequests.Should().ContainSingle("the shared installer should be sent to the AI only once");
        verifier.LastRequests[0].CorrelationId.Should().Be(sharedId);
        vm.Drivers.Should().OnlyContain(r => r.AvailableUpdate != null && r.AvailableUpdate.AiVerification == verdict,
            "every row sharing the installer should receive the same verdict");
    }

    [WpfFact]
    public async Task ScanAsync_leaves_results_unchanged_when_ai_returns_no_verdicts()
    {
        var driver = NewDriver("AMD Radeon", "PCI\\VEN_1002&DEV_747E", new Version(1, 0, 0, 0));
        var candidate = NewCandidate("PCI\\VEN_1002&DEV_747E", new Version(2, 0, 0, 0), "amd-update");
        var verifier = new StubAiVerifier(isConfigured: true);

        var vm = NewVm(new[] { driver }, new[] { candidate }, verifier);
        await vm.ScanCommand.ExecuteAsync(null);

        verifier.WasCalled.Should().BeTrue();
        vm.Drivers[0].AvailableUpdate.Should().NotBeNull();
        vm.Drivers[0].AvailableUpdate!.AiVerification.Should().BeNull();
        vm.Drivers[0].Status.Should().Be(DriverStatus.Outdated);
        vm.UpdatesFoundCount.Should().Be(1);
    }

    [WpfFact]
    public async Task ScanAsync_does_not_verify_vendor_page_candidates()
    {
        var driver = NewDriver("NVIDIA Display", "PCI\\VEN_10DE&DEV_0001", new Version(1, 0, 0, 0));
        var advisory = NewCandidate(
            "PCI\\VEN_10DE&DEV_0001",
            new Version(2026, 5, 28, 0),
            "nvidia-advisory",
            UpdateInstallKind.VendorPage,
            UpdateConfidence.Advisory);
        var verifier = new StubAiVerifier(isConfigured: true);

        var vm = NewVm(new[] { driver }, new[] { advisory }, verifier);
        await vm.ScanCommand.ExecuteAsync(null);

        verifier.WasCalled.Should().BeFalse("vendor-page advisories are not sent to AI verification");
        vm.Drivers[0].AvailableUpdate.Should().NotBeNull();
    }

    private static MainViewModel NewVm(
        IEnumerable<DriverInfo> drivers,
        IEnumerable<UpdateCandidate> candidates,
        IAiVerifier aiVerifier) =>
        new(new FakeScanService(drivers),
            new[] { (IUpdateSource)new FakeUpdateSource(candidates) },
            new NullOemDetectionService(),
            new NullInstallPipeline(),
            new NullInstallConfirmation(),
            new NullHistoryWindowOpener(),
            new NullSettingsWindowOpener(),
            new NullLogsWindowOpener(),
            NullLogger<MainViewModel>.Instance,
            aiVerifier: aiVerifier);

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

    private static UpdateCandidate NewCandidate(
        string hardwareId,
        Version newVersion,
        string sourceUpdateId,
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
        SourceUpdateId: sourceUpdateId,
        SupersededIds: Array.Empty<string>(),
        InstallKind: installKind,
        Confidence: confidence);

    private sealed class StubAiVerifier : IAiVerifier
    {
        public StubAiVerifier(bool isConfigured)
        {
            IsConfigured = isConfigured;
        }

        public AiProvider Provider => AiProvider.Gemini;
        public bool IsConfigured { get; }
        public bool Throws { get; set; }
        public bool WasCalled { get; private set; }
        public IReadOnlyList<AiVerificationRequest> LastRequests { get; private set; } = Array.Empty<AiVerificationRequest>();
        public Dictionary<string, AiVerdict> Verdicts { get; } = new();

        public Task<IReadOnlyDictionary<string, AiVerdict>> VerifyAsync(
            IReadOnlyList<AiVerificationRequest> requests, CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            LastRequests = requests;
            if (Throws)
            {
                throw new InvalidOperationException("ai failed");
            }
            return Task.FromResult((IReadOnlyDictionary<string, AiVerdict>)Verdicts);
        }
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
