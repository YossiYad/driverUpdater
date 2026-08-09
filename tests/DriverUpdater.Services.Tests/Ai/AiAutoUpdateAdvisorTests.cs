using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using DriverUpdater.Services.Ai;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriverUpdater.Services.Tests.Ai;

public class AiAutoUpdateAdvisorTests
{
    [Fact]
    public async Task A_safe_genuine_upgrade_is_approved()
    {
        var verifier = new StubVerifier(("update-1", Verdict(true, AiRiskLevel.Safe)));
        var advisor = NewAdvisor(verifier);

        var decisions = await advisor.ReviewAsync(
            new[] { Item("update-1") },
            AiAutoUpdateRiskTolerance.SafeOnly);

        decisions.Should().ContainSingle().Which.ShouldInstall.Should().BeTrue();
        decisions[0].Verdict.Should().NotBeNull("the runner stores the reasoning on the candidate");
    }

    [Fact]
    public async Task An_update_the_ai_does_not_consider_newer_is_rejected()
    {
        var verifier = new StubVerifier(("update-1", Verdict(false, AiRiskLevel.Safe)));
        var advisor = NewAdvisor(verifier);

        var decisions = await advisor.ReviewAsync(
            new[] { Item("update-1") },
            AiAutoUpdateRiskTolerance.SafeAndCaution);

        decisions.Should().ContainSingle().Which.ShouldInstall.Should().BeFalse();
    }

    [Theory]
    [InlineData(AiRiskLevel.Caution, AiAutoUpdateRiskTolerance.SafeOnly, false)]
    [InlineData(AiRiskLevel.Caution, AiAutoUpdateRiskTolerance.SafeAndCaution, true)]
    [InlineData(AiRiskLevel.HighRisk, AiAutoUpdateRiskTolerance.SafeAndCaution, false)]
    [InlineData(AiRiskLevel.Unknown, AiAutoUpdateRiskTolerance.SafeAndCaution, false)]
    [InlineData(AiRiskLevel.Safe, AiAutoUpdateRiskTolerance.SafeOnly, true)]
    public async Task Risk_is_weighed_against_the_configured_tolerance(
        AiRiskLevel risk,
        AiAutoUpdateRiskTolerance tolerance,
        bool expected)
    {
        var advisor = NewAdvisor(new StubVerifier(("update-1", Verdict(true, risk))));

        var decisions = await advisor.ReviewAsync(new[] { Item("update-1") }, tolerance);

        decisions.Should().ContainSingle().Which.ShouldInstall.Should().Be(expected);
    }

    [Fact]
    public async Task An_update_without_a_verdict_is_rejected()
    {
        var advisor = NewAdvisor(new StubVerifier());

        var decisions = await advisor.ReviewAsync(
            new[] { Item("update-1") },
            AiAutoUpdateRiskTolerance.SafeAndCaution);

        var decision = decisions.Should().ContainSingle().Subject;
        decision.ShouldInstall.Should().BeFalse("nobody is watching, so no answer means do not install");
        decision.Verdict.Should().BeNull();
    }

    [Fact]
    public async Task One_installer_shared_by_many_devices_is_reviewed_once()
    {
        var verifier = new StubVerifier(("shared", Verdict(true, AiRiskLevel.Safe)));
        var advisor = NewAdvisor(verifier);

        var decisions = await advisor.ReviewAsync(
            new[] { Item("shared", "AMD Tools"), Item("shared", "AMD SMBus"), Item("shared", "AMD PSP") },
            AiAutoUpdateRiskTolerance.SafeOnly);

        verifier.Requests.Should().ContainSingle("a shared package must cost one AI request, not three");
        decisions.Should().ContainSingle().Which.SourceUpdateId.Should().Be("shared");
    }

    [Fact]
    public async Task No_items_means_no_ai_call()
    {
        var verifier = new StubVerifier();
        var advisor = NewAdvisor(verifier);

        var decisions = await advisor.ReviewAsync(
            Array.Empty<AiUpdateReviewItem>(),
            AiAutoUpdateRiskTolerance.SafeOnly);

        decisions.Should().BeEmpty();
        verifier.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task The_request_carries_the_installed_and_candidate_versions()
    {
        var verifier = new StubVerifier(("update-1", Verdict(true, AiRiskLevel.Safe)));
        var advisor = NewAdvisor(verifier);

        await advisor.ReviewAsync(new[] { Item("update-1") }, AiAutoUpdateRiskTolerance.SafeOnly);

        var request = verifier.Requests.Should().ContainSingle().Subject;
        request.CorrelationId.Should().Be("update-1");
        request.InstalledVersion.Should().Be("1.0.0.0");
        request.CandidateVersion.Should().Be("2.0.0.0");
        request.HardwareId.Should().Be(@"PCI\VEN_8086&DEV_4682");
        request.FindLatestWhenNoCandidate.Should().BeFalse();
    }

    [Fact]
    public void IsConfigured_follows_the_verifier()
    {
        NewAdvisor(new StubVerifier { Configured = false }).IsConfigured.Should().BeFalse();
        NewAdvisor(new StubVerifier()).IsConfigured.Should().BeTrue();
    }

    private static AiAutoUpdateAdvisor NewAdvisor(IAiVerifier verifier) =>
        new(verifier, NullLogger<AiAutoUpdateAdvisor>.Instance);

    private static AiVerdict Verdict(bool genuinelyNewer, AiRiskLevel risk) =>
        new(genuinelyNewer, risk, "ok", "because", "2.0.0.0");

    private static AiUpdateReviewItem Item(string sourceUpdateId, string deviceName = "Intel Display") =>
        new(
            new DriverInfo(
                DeviceId: $@"ID\{deviceName}",
                HardwareId: @"PCI\VEN_8086&DEV_4682",
                DeviceName: deviceName,
                Category: DriverCategory.Display,
                Provider: "Intel",
                Manufacturer: "Intel",
                CurrentVersion: new Version(1, 0, 0, 0),
                CurrentDate: new DateOnly(2024, 1, 1),
                InfName: "oem.inf",
                InfPath: null,
                IsSigned: true,
                DeviceClass: "Display"),
            new UpdateCandidate(
                ForHardwareId: @"PCI\VEN_8086&DEV_4682",
                Source: UpdateSource.MicrosoftCatalog,
                NewVersion: new Version(2, 0, 0, 0),
                NewDate: new DateOnly(2026, 1, 1),
                DownloadUrl: new Uri("https://example.com/x.cab"),
                SizeBytes: 1024,
                KbArticle: null,
                IsSuperseded: false,
                SourceUpdateId: sourceUpdateId,
                SupersededIds: Array.Empty<string>(),
                InstallKind: UpdateInstallKind.PnPUtilPackage));

    [Fact]
    public async Task ReviewAsync_tells_the_verifier_that_nobody_is_watching()
    {
        // A scheduled run installs without anyone to read a warning, so the prompt has to hold
        // the model to a stricter bar than an interactive "Scan with AI".
        var verifier = new StubVerifier(("update-1", Verdict(true, AiRiskLevel.Safe)));
        var advisor = NewAdvisor(verifier);

        await advisor.ReviewAsync(new[] { Item("update-1") }, AiAutoUpdateRiskTolerance.SafeOnly);

        verifier.LastUnattendedRun.Should().BeTrue();
    }

    private sealed class StubVerifier : IAiVerifier
    {
        private readonly Dictionary<string, AiVerdict> _verdicts;

        public StubVerifier(params (string Id, AiVerdict Verdict)[] verdicts) =>
            _verdicts = verdicts.ToDictionary(v => v.Id, v => v.Verdict, StringComparer.OrdinalIgnoreCase);

        public List<AiVerificationRequest> Requests { get; } = new();

        public AiProvider Provider => AiProvider.Gemini;

        public bool Configured { get; init; } = true;

        public bool IsConfigured => Configured;

        public bool IsTemporarilyUnavailable => false;

        public bool? LastUnattendedRun { get; private set; }

        public Task<IReadOnlyDictionary<string, AiVerdict>> VerifyAsync(
            IReadOnlyList<AiVerificationRequest> requests,
            bool unattendedRun = false,
            CancellationToken cancellationToken = default)
        {
            Requests.AddRange(requests);
            LastUnattendedRun = unattendedRun;
            return Task.FromResult<IReadOnlyDictionary<string, AiVerdict>>(_verdicts);
        }
    }
}
