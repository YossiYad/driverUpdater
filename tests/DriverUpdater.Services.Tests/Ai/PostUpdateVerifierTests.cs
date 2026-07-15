using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using DriverUpdater.Services.Ai;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriverUpdater.Services.Tests.Ai;

public class PostUpdateVerifierTests
{
    [Fact]
    public async Task Changed_active_driver_is_verified_and_ai_summarizes_measured_result()
    {
        var probe = new FakeProbe(new InstalledDriverState(new Version(2, 0, 0, 0), new DateOnly(2026, 1, 1)));
        var ai = new FakeCompleter(isConfigured: true, response: "העדכון הצליח.");
        var verifier = NewVerifier(probe, ai);

        var report = await verifier.VerifyAsync(
            NewBatch(NewOperation(UpdateStatus.Succeeded)),
            isAfterRestart: false,
            AppLanguage.Hebrew);

        report.Items.Should().ContainSingle().Which.Status.Should().Be(UpdateVerificationStatus.VerifiedUpdated);
        report.Items[0].CurrentVersion.Should().Be(new Version(2, 0, 0, 0));
        report.AiWasUsed.Should().BeTrue();
        report.AiSummary.Should().Be("העדכון הצליח.");
        ai.LastPrompt.Should().Contain("Write the answer in clear, natural Hebrew.");
        ai.LastPrompt.Should().Contain("Windows read-back results");
        ai.LastPrompt.Should().Contain("Installer process result: Succeeded");
        ai.LastPrompt.Should().Contain("Delivery type: WindowsUpdate");
    }

    [Fact]
    public async Task Restart_required_is_pending_before_restart_without_reading_driver()
    {
        var probe = new FakeProbe(new InstalledDriverState(new Version(2, 0), null));
        var verifier = NewVerifier(probe, new FakeCompleter(false, null));

        var report = await verifier.VerifyAsync(
            NewBatch(NewOperation(UpdateStatus.Succeeded, "Reboot required to complete installation.")),
            isAfterRestart: false,
            AppLanguage.English);

        report.Items.Should().ContainSingle().Which.Status.Should().Be(UpdateVerificationStatus.PendingRestart);
        probe.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Unchanged_driver_after_restart_is_reported_as_not_updated()
    {
        var probe = new FakeProbe(new InstalledDriverState(new Version(1, 0, 0, 0), new DateOnly(2025, 1, 1)));
        var verifier = NewVerifier(probe, new FakeCompleter(false, null));

        var report = await verifier.VerifyAsync(
            NewBatch(NewOperation(UpdateStatus.Succeeded, "Reboot required to complete installation.")),
            isAfterRestart: true,
            AppLanguage.English);

        report.Items.Should().ContainSingle().Which.Status.Should().Be(UpdateVerificationStatus.NotUpdated);
        probe.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task Summary_prompt_distinguishes_a_completed_installer_from_a_verified_driver_change()
    {
        var probe = new FakeProbe(new InstalledDriverState(
            new Version(1, 0, 0, 0),
            new DateOnly(2025, 1, 1)));
        var ai = new FakeCompleter(true, "The installer ran, but Windows did not show a driver change.");
        var verifier = NewVerifier(probe, ai);

        await verifier.VerifyAsync(
            NewBatch(NewOperation(UpdateStatus.Succeeded)),
            isAfterRestart: false,
            AppLanguage.English);

        ai.LastPrompt.Should().Contain("successful installer process is not the same as a verified driver change");
        ai.LastPrompt.Should().Contain("Do not say that no automatic installation was attempted");
        ai.LastPrompt.Should().Contain("Verified result: NotUpdated");
        ai.LastPrompt.Should().Contain("Installer process result: Succeeded");
    }

    [Fact]
    public async Task Failed_install_reads_back_windows_before_reporting_the_previous_driver()
    {
        var probe = new FakeProbe(new InstalledDriverState(new Version(1, 0, 0, 0), new DateOnly(2025, 1, 1)));
        var verifier = NewVerifier(probe, new FakeCompleter(false, null));

        var report = await verifier.VerifyAsync(
            NewBatch(NewOperation(UpdateStatus.Failed, "Installer failed")),
            isAfterRestart: false,
            AppLanguage.English);

        report.Items.Should().ContainSingle().Which.Status.Should().Be(UpdateVerificationStatus.Failed);
        probe.CallCount.Should().Be(1);
        report.Items[0].CurrentVersion.Should().Be(new Version(1, 0, 0, 0));
        report.AiWasUsed.Should().BeFalse();
    }

    [Fact]
    public async Task Vendor_page_fallback_is_manual_action_not_failed_install()
    {
        var probe = new FakeProbe(null);
        var verifier = NewVerifier(probe, new FakeCompleter(false, null));
        var operation = NewOperation(UpdateStatus.Skipped, "Open the official vendor page to install this update") with
        {
            Candidate = NewOperation(UpdateStatus.Skipped).Candidate with
            {
                InstallKind = UpdateInstallKind.VendorPage,
                Confidence = UpdateConfidence.Advisory,
                DownloadUrl = new Uri("https://vendor.example.com/support")
            }
        };

        var report = await verifier.VerifyAsync(
            NewBatch(operation),
            isAfterRestart: false,
            AppLanguage.English);

        report.Items.Should().ContainSingle().Which.Status.Should().Be(UpdateVerificationStatus.ManualActionRequired);
        report.Items[0].CurrentVersion.Should().Be(new Version(1, 0, 0, 0));
        report.Items[0].ActionUrl.Should().Be(new Uri("https://vendor.example.com/support"));
        probe.CallCount.Should().Be(0);
    }

    private static PostUpdateVerifier NewVerifier(IInstalledDriverProbe probe, IAiTextCompleter ai) =>
        new(probe, ai, NullLogger<PostUpdateVerifier>.Instance);

    private static PendingUpdateVerificationBatch NewBatch(UpdateOperation operation) =>
        new(Guid.NewGuid(), DateTimeOffset.UtcNow, new[] { operation });

    private static UpdateOperation NewOperation(UpdateStatus status, string? error = null)
    {
        var driver = new DriverInfo(
            DeviceId: "DEVICE\\1",
            HardwareId: "HARDWARE\\1",
            DeviceName: "Test display",
            Category: DriverCategory.Display,
            Provider: "Vendor",
            Manufacturer: "Vendor",
            CurrentVersion: new Version(1, 0, 0, 0),
            CurrentDate: new DateOnly(2025, 1, 1),
            InfName: "oem1.inf",
            InfPath: null,
            IsSigned: true,
            DeviceClass: "Display");
        var candidate = new UpdateCandidate(
            ForHardwareId: driver.HardwareId,
            Source: UpdateSource.WindowsUpdate,
            NewVersion: new Version(2, 0, 0, 0),
            NewDate: new DateOnly(2026, 1, 1),
            DownloadUrl: new Uri("about:blank"),
            SizeBytes: 0,
            KbArticle: null,
            IsSuperseded: false,
            SourceUpdateId: "update-1",
            SupersededIds: Array.Empty<string>());
        return UpdateOperation.NewPending(candidate, driver) with
        {
            Status = status,
            ErrorMessage = error,
            CompletedAt = DateTimeOffset.UtcNow
        };
    }

    private sealed class FakeProbe : IInstalledDriverProbe
    {
        private readonly InstalledDriverState? _state;

        public FakeProbe(InstalledDriverState? state) => _state = state;

        public int CallCount { get; private set; }

        public Task<InstalledDriverState?> GetCurrentAsync(
            string deviceId,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(_state);
        }
    }

    private sealed class FakeCompleter : IAiTextCompleter
    {
        private readonly string? _response;

        public FakeCompleter(bool isConfigured, string? response)
        {
            IsConfigured = isConfigured;
            _response = response;
        }

        public AiProvider Provider => AiProvider.Gemini;
        public bool IsConfigured { get; }
        public string? LastPrompt { get; private set; }

        public Task<string?> CompleteAsync(string prompt, CancellationToken cancellationToken = default)
        {
            LastPrompt = prompt;
            return Task.FromResult(_response);
        }
    }
}
