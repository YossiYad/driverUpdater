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
    public async Task ScanAsync_does_not_auto_verify_vendor_page_candidates()
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

        verifier.WasCalled.Should().BeFalse("vendor-page advisories should only be checked when the user clicks Ask AI");
        vm.Drivers[0].AvailableUpdate.Should().NotBeNull();
    }

    [WpfFact]
    public async Task AskAiAsync_reviews_only_selected_update_and_adds_recommendation()
    {
        var driver = NewDriver("Intel Network", "PCI\\VEN_8086&DEV_1234", new Version(1, 0, 0, 0));
        var candidate = NewCandidate("PCI\\VEN_8086&DEV_1234", new Version(2, 0, 0, 0), "intel-net");
        var verdict = new AiVerdict(true, AiRiskLevel.Safe, "Recommended", "No major regressions found for this hardware.", "2.0.0.0");
        var verifier = new StubAiVerifier(isConfigured: true);

        var vm = NewVm(new[] { driver }, new[] { candidate }, verifier);
        await vm.ScanCommand.ExecuteAsync(null);
        verifier.Reset();
        verifier.Verdicts["intel-net"] = verdict;

        await vm.AskAiCommand.ExecuteAsync(vm.Drivers[0]);

        verifier.WasCalled.Should().BeTrue();
        verifier.LastRequests.Should().ContainSingle();
        verifier.LastRequests[0].CorrelationId.Should().Be("intel-net");
        verifier.LastRequests[0].Category.Should().Be(driver.Category);
        verifier.LastRequests[0].Provider.Should().Be(driver.Provider);
        verifier.LastRequests[0].InstallKind.Should().Be(candidate.InstallKind);
        vm.Drivers[0].AvailableUpdate!.AiVerification.Should().Be(verdict);
        vm.Drivers[0].AiRecommendationText.Should().Be("Recommended");
        vm.StatusText.Should().Contain("AI recommendation");
    }

    [WpfFact]
    public async Task AskAiAsync_removes_update_when_ai_says_not_genuinely_newer()
    {
        var driver = NewDriver("Realtek Audio", "PCI\\VEN_10EC&DEV_8168", new Version(1, 0, 0, 0));
        var candidate = NewCandidate("PCI\\VEN_10EC&DEV_8168", new Version(2, 0, 0, 0), "realtek-audio");
        var verifier = new StubAiVerifier(isConfigured: true);

        var vm = NewVm(new[] { driver }, new[] { candidate }, verifier);
        await vm.ScanCommand.ExecuteAsync(null);
        verifier.Reset();
        verifier.Verdicts["realtek-audio"] = new AiVerdict(false, AiRiskLevel.Safe, "Already current", "The package appears to be the same driver.", "1.0.0.0");

        await vm.AskAiCommand.ExecuteAsync(vm.Drivers[0]);

        vm.Drivers[0].AvailableUpdate.Should().BeNull();
        vm.Drivers[0].Status.Should().Be(DriverStatus.UpToDate);
        vm.UpdatesFoundCount.Should().Be(0);
        vm.StatusText.Should().Contain("does not recommend");
    }

    [WpfFact]
    public async Task AskAiAsync_without_existing_update_finds_latest_driver_and_adds_vendor_check()
    {
        var driver = NewDriver("Intel Network", "PCI\\VEN_8086&DEV_1234", new Version(1, 0, 0, 0));
        var verifier = new StubAiVerifier(isConfigured: true);
        var vm = NewVm(new[] { driver }, Array.Empty<UpdateCandidate>(), verifier);
        await vm.ScanCommand.ExecuteAsync(null);
        verifier.Reset();
        verifier.Verdicts["ai-latest:PCI\\VEN_8086&DEV_1234"] = new AiVerdict(
            true,
            AiRiskLevel.Safe,
            "Recommended",
            "Intel lists a newer package for this hardware.",
            "2.0.0.0",
            new DateOnly(2026, 2, 3),
            "https://example.com/intel-driver");

        await vm.AskAiCommand.ExecuteAsync(vm.Drivers[0]);

        verifier.WasCalled.Should().BeTrue();
        verifier.LastRequests.Should().ContainSingle();
        verifier.LastRequests[0].FindLatestWhenNoCandidate.Should().BeTrue();
        verifier.LastRequests[0].CorrelationId.Should().Be("ai-latest:PCI\\VEN_8086&DEV_1234");
        vm.Drivers[0].AvailableUpdate.Should().NotBeNull();
        vm.Drivers[0].AvailableUpdate!.InstallKind.Should().Be(UpdateInstallKind.VendorPage);
        vm.Drivers[0].AvailableUpdate!.Confidence.Should().Be(UpdateConfidence.Advisory);
        vm.Drivers[0].AvailableUpdate!.NewVersion.Should().Be(new Version(2, 0, 0, 0));
        vm.Drivers[0].AvailableUpdate!.DownloadUrl.Should().Be(new Uri("https://example.com/intel-driver"));
        vm.Drivers[0].AiRecommendationText.Should().Be("Recommended");
        vm.VendorChecksCount.Should().Be(1);
        vm.StatusText.Should().Contain("AI found a newer driver");
    }

    [WpfFact]
    public async Task AskAiAsync_without_existing_update_keeps_row_unchanged_when_latest_is_current()
    {
        var driver = NewDriver("Intel Network", "PCI\\VEN_8086&DEV_1234", new Version(1, 0, 0, 0));
        var verifier = new StubAiVerifier(isConfigured: true);
        var vm = NewVm(new[] { driver }, Array.Empty<UpdateCandidate>(), verifier);
        await vm.ScanCommand.ExecuteAsync(null);
        verifier.Reset();
        verifier.Verdicts["ai-latest:PCI\\VEN_8086&DEV_1234"] = new AiVerdict(
            false,
            AiRiskLevel.Safe,
            "Already current",
            "No newer official package was found.",
            "1.0.0.0");

        await vm.AskAiCommand.ExecuteAsync(vm.Drivers[0]);

        vm.Drivers[0].AvailableUpdate.Should().BeNull();
        vm.VendorChecksCount.Should().Be(0);
        vm.StatusText.Should().Contain("did not find a newer official driver");
    }

    [WpfFact]
    public async Task AskAiAsync_without_existing_update_does_not_create_vendor_check_for_documentation_only_result()
    {
        var driver = new DriverInfo(
            DeviceId: "SWD\\MIDISRV\\MIDIU_APP_TRANSPORT",
            HardwareId: "SWD\\MIDISRV\\MIDIU_APP_TRANSPORT",
            DeviceName: "Generic software device",
            Category: DriverCategory.System,
            Provider: "Microsoft",
            Manufacturer: "Microsoft",
            CurrentVersion: new Version(10, 0, 26100, 1),
            CurrentDate: new DateOnly(2006, 6, 21),
            InfName: "midi.inf",
            InfPath: null,
            IsSigned: true,
            DeviceClass: "System");
        var verifier = new StubAiVerifier(isConfigured: true);
        var vm = NewVm(new[] { driver }, Array.Empty<UpdateCandidate>(), verifier);
        await vm.ScanCommand.ExecuteAsync(null);
        verifier.Reset();
        verifier.Verdicts["ai-latest:SWD\\MIDISRV\\MIDIU_APP_TRANSPORT"] = new AiVerdict(
            true,
            AiRiskLevel.Caution,
            "Use caution",
            "A documentation page mentions a newer Windows component build.",
            "10.0.26100.6899",
            new DateOnly(2026, 2, 15),
            "https://learn.microsoft.com/en-us/windows/win32/midi/windows-midi-services",
            InstalledSuitability: "The installed component is inbox Windows software.",
            CandidateSuitability: "The cited page is documentation, not a driver update package.",
            RecommendedVersion: "10.0.26100.6899",
            AdvisorNote: "Use Windows Update or a stable Windows release; do not treat this as a standalone driver download.");

        await vm.AskAiCommand.ExecuteAsync(vm.Drivers[0]);

        verifier.WasCalled.Should().BeTrue();
        vm.Drivers[0].AvailableUpdate.Should().BeNull();
        vm.VendorChecksCount.Should().Be(0);
        vm.StatusText.Should().Contain("did not find a newer official driver");
    }

    [WpfFact]
    public async Task ScanAsync_uses_ai_to_discover_latest_drivers_when_sources_find_nothing()
    {
        var driver = NewDriver("Intel Network", "PCI\\VEN_8086&DEV_1234", new Version(1, 0, 0, 0));
        var verifier = new StubAiVerifier(isConfigured: true)
        {
            Verdicts =
            {
                ["ai-latest:PCI\\VEN_8086&DEV_1234"] = new AiVerdict(
                    true,
                    AiRiskLevel.Safe,
                    "Recommended",
                    "Intel lists a newer package for this hardware.",
                    "2.0.0.0",
                    new DateOnly(2026, 2, 3),
                    "https://example.com/intel-driver")
            }
        };
        var vm = NewVm(new[] { driver }, Array.Empty<UpdateCandidate>(), verifier);

        await vm.ScanCommand.ExecuteAsync(null);

        verifier.WasCalled.Should().BeTrue();
        verifier.LastRequests.Should().ContainSingle();
        verifier.LastRequests[0].FindLatestWhenNoCandidate.Should().BeTrue();
        vm.Drivers[0].AvailableUpdate.Should().NotBeNull();
        vm.Drivers[0].AvailableUpdate!.InstallKind.Should().Be(UpdateInstallKind.VendorPage);
        vm.VendorChecksCount.Should().Be(1);
    }

    [WpfFact]
    public async Task ScanAsync_ai_discovery_does_not_offer_calendar_downgrade_of_windows_inbox_driver()
    {
        // Regression: the AI discovery path bypassed IsNewerThan, so the AI could reintroduce a
        // calendar-versioned OEM driver (e.g. 2018.7.17.0) over a modern Windows inbox driver
        // (10.0.26100.x) — exactly the recurring downgrade seen in the field.
        var driver = new DriverInfo(
            DeviceId: "PCI\\INTEL_CPU",
            HardwareId: "PCI\\VEN_8086&DEV_CPU",
            DeviceName: "Intel Processor",
            Category: DriverCategory.Chipset,
            Provider: "Microsoft",
            Manufacturer: "Intel",
            CurrentVersion: new Version(10, 0, 26100, 8521),
            CurrentDate: new DateOnly(2024, 1, 1),
            InfName: "cpu.inf",
            InfPath: null,
            IsSigned: true,
            DeviceClass: "Processor");
        var verifier = new StubAiVerifier(isConfigured: true)
        {
            Verdicts =
            {
                ["ai-latest:PCI\\VEN_8086&DEV_CPU"] = new AiVerdict(
                    true, AiRiskLevel.Safe, "found", "An older OEM package exists.",
                    "2018.7.17.0", new DateOnly(2018, 7, 17), "https://example.com/intel-cpu-2018")
            }
        };
        var vm = NewVm(new[] { driver }, Array.Empty<UpdateCandidate>(), verifier);

        await vm.ScanCommand.ExecuteAsync(null);

        verifier.WasCalled.Should().BeTrue();
        vm.Drivers[0].AvailableUpdate.Should().BeNull(
            "a 2018 calendar-versioned driver must never be offered over a Windows inbox driver");
        vm.VendorChecksCount.Should().Be(0);
    }

    [WpfFact]
    public async Task AskAiAllAsync_verifies_existing_updates_and_discovers_missing_updates()
    {
        var candidateDriver = NewDriver("Realtek Audio", "PCI\\VEN_10EC&DEV_8168", new Version(1, 0, 0, 0));
        var missingDriver = NewDriver("Intel Network", "PCI\\VEN_8086&DEV_1234", new Version(1, 0, 0, 0));
        var candidate = NewCandidate(candidateDriver.HardwareId, new Version(2, 0, 0, 0), "realtek-audio");
        var verifier = new StubAiVerifier(isConfigured: true)
        {
            Verdicts =
            {
                ["realtek-audio"] = new AiVerdict(true, AiRiskLevel.Caution, "Use caution", "Mixed reports.", "2.0.0.0"),
                ["ai-latest:PCI\\VEN_8086&DEV_1234"] = new AiVerdict(
                    true,
                    AiRiskLevel.Safe,
                    "Recommended",
                    "Intel lists a newer package for this hardware.",
                    "3.0.0.0",
                    new DateOnly(2026, 3, 4),
                    "https://example.com/intel-driver")
            }
        };
        var vm = NewVm(Array.Empty<DriverInfo>(), Array.Empty<UpdateCandidate>(), verifier);
        vm.Drivers.Add(new DriverRowViewModel(candidateDriver)
        {
            Status = DriverStatus.Outdated,
            AvailableUpdate = candidate
        });
        vm.Drivers.Add(new DriverRowViewModel(missingDriver));
        vm.ScannedCount = 2;

        await vm.AskAiAllCommand.ExecuteAsync(null);

        vm.Drivers[0].AvailableUpdate!.AiVerification.Should().NotBeNull();
        vm.Drivers[0].AiRiskText.Should().Be("Caution");
        vm.Drivers[1].AvailableUpdate.Should().NotBeNull();
        vm.Drivers[1].AvailableUpdate!.InstallKind.Should().Be(UpdateInstallKind.VendorPage);
        vm.VendorChecksCount.Should().Be(1);
        vm.StatusText.Should().Contain("AI latest-driver search complete");
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

        public void Reset()
        {
            WasCalled = false;
            LastRequests = Array.Empty<AiVerificationRequest>();
        }

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
