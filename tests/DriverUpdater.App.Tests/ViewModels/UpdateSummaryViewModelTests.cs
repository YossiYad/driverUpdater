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

        vm.ManualActionCountText.Should().Contain("1 without a safe in-app installer");
        vm.SummaryText.Should().Contain("No safe in-app installer was found");
        vm.Items[0].StatusText.Should().Be("No safe in-app installer");
        vm.Items[0].Explanation.Should().Contain("No external page was opened");
        vm.Items[0].VersionText.Should().Contain("No automatic change was made");
    }

    [Fact]
    public void Failed_update_with_changed_readback_does_not_claim_previous_driver_is_active()
    {
        var item = new UpdateVerificationItem(
            Guid.NewGuid(),
            "NVIDIA graphics",
            DriverCategory.Display,
            new Version(32, 0, 16, 1047),
            new DateOnly(2026, 7, 15),
            new Version(32, 0, 16, 1074),
            new DateOnly(2026, 7, 2),
            new Version(32, 0, 16, 1074),
            new DateOnly(2026, 7, 2),
            UpdateVerificationStatus.Failed,
            "Conflicting metadata",
            UpdateStatus.Failed,
            UpdateInstallKind.VendorInstaller,
            UpdateConfidence.Confirmed,
            null);
        var report = new UpdateVerificationReport(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            false,
            new[] { item },
            null,
            false);

        var vm = new UpdateSummaryViewModel(report, AppLanguage.English);

        vm.Items[0].Explanation.Should().Contain("different driver metadata");
        vm.Items[0].Explanation.Should().NotContain("previous driver is still active");
    }

    [Theory]
    [InlineData(AppLanguage.English, "Windows kept the previous driver")]
    [InlineData(AppLanguage.Hebrew, "המשיך להשתמש בדרייבר הקודם")]
    public void NotUpdated_does_not_claim_the_component_was_already_current(AppLanguage language, string expected)
    {
        // WinUsb Device: installed 1.1.0.0, target 2021.12.29.0, still 1.1.0.0 after the restart.
        // The package went into the driver store and Windows declined to bind it.
        var report = NewNotUpdatedReport(
            previous: new Version(1, 1, 0, 0),
            expectedVersion: new Version(2021, 12, 29, 0),
            current: new Version(1, 1, 0, 0));

        var vm = new UpdateSummaryViewModel(report, language);

        vm.Items[0].Explanation.Should().Contain(expected);
    }

    [Fact]
    public void NotUpdated_still_says_already_current_when_the_target_version_is_the_active_one()
    {
        var report = NewNotUpdatedReport(
            previous: new Version(2, 0),
            expectedVersion: new Version(2, 0),
            current: new Version(2, 0));

        var vm = new UpdateSummaryViewModel(report, AppLanguage.English);

        vm.Items[0].Explanation.Should().Contain("already at the requested version");
    }

    private static UpdateVerificationReport NewNotUpdatedReport(
        Version previous,
        Version expectedVersion,
        Version current)
    {
        var item = new UpdateVerificationItem(
            Guid.NewGuid(),
            "WinUsb Device",
            DriverCategory.Other,
            previous,
            new DateOnly(2006, 6, 21),
            expectedVersion,
            new DateOnly(2021, 12, 29),
            current,
            new DateOnly(2006, 6, 21),
            UpdateVerificationStatus.NotUpdated,
            null,
            UpdateStatus.Succeeded,
            UpdateInstallKind.PnPUtilPackage,
            UpdateConfidence.Confirmed,
            null);
        return new UpdateVerificationReport(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            true,
            new[] { item },
            null,
            false);
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
