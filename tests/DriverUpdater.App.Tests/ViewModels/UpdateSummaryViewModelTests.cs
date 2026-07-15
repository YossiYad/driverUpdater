using DriverUpdater.App.ViewModels;
using DriverUpdater.Core.Models;
using FluentAssertions;

namespace DriverUpdater.App.Tests.ViewModels;

public class UpdateSummaryViewModelTests
{
    [Fact]
    public void Hebrew_summary_uses_plain_friendly_language_for_verified_update()
    {
        var report = NewReport(
            UpdateVerificationStatus.VerifiedUpdated,
            aiSummary: "Windows בדק את העדכון והוא פעיל.",
            aiWasUsed: true);

        var vm = new UpdateSummaryViewModel(report, AppLanguage.Hebrew);

        vm.Header.Should().Contain("העדכון הסתיים");
        vm.AiLabel.Should().Contain("AI");
        vm.Items.Should().ContainSingle();
        vm.Items[0].StatusText.Should().Be("עודכן בהצלחה");
        vm.Items[0].Explanation.Should().Contain("Windows אישר");
    }

    [Fact]
    public void Missing_ai_response_falls_back_to_deterministic_summary()
    {
        var report = NewReport(
            UpdateVerificationStatus.PendingRestart,
            aiSummary: null,
            aiWasUsed: false);

        var vm = new UpdateSummaryViewModel(report, AppLanguage.English);

        vm.AiLabel.Should().Contain("AI is not configured");
        vm.SummaryText.Should().Contain("after the computer restarts");
        vm.Items[0].StatusText.Should().Be("Waiting for restart");
    }

    [Fact]
    public void Manual_vendor_follow_up_is_not_described_as_failed_or_uninstalled()
    {
        var report = NewReport(
            UpdateVerificationStatus.ManualActionRequired,
            aiSummary: null,
            aiWasUsed: false);

        var vm = new UpdateSummaryViewModel(report, AppLanguage.English);

        vm.ManualActionCountText.Should().Contain("1 continue on vendor website");
        vm.SummaryText.Should().Contain("No automatic installation was attempted");
        vm.Items[0].StatusText.Should().Be("Continue on vendor website");
        vm.Items[0].Explanation.Should().Contain("vendor page was opened");
        vm.Items[0].VersionText.Should().Contain("No automatic change was made");
    }

    private static UpdateVerificationReport NewReport(
        UpdateVerificationStatus status,
        string? aiSummary,
        bool aiWasUsed)
    {
        var item = new UpdateVerificationItem(
            Guid.NewGuid(),
            "Intel graphics",
            DriverCategory.Display,
            new Version(1, 0),
            new DateOnly(2025, 1, 1),
            new Version(2, 0),
            new DateOnly(2026, 1, 1),
            status == UpdateVerificationStatus.VerifiedUpdated ? new Version(2, 0) : null,
            status == UpdateVerificationStatus.VerifiedUpdated ? new DateOnly(2026, 1, 1) : null,
            status,
            null,
            UpdateStatus.Succeeded,
            UpdateInstallKind.WindowsUpdate,
            UpdateConfidence.Confirmed,
            null);
        return new UpdateVerificationReport(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            false,
            new[] { item },
            aiSummary,
            aiWasUsed);
    }
}
