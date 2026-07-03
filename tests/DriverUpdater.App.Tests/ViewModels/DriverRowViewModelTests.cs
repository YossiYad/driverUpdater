using DriverUpdater.App.ViewModels;
using DriverUpdater.Core.Models;
using FluentAssertions;

namespace DriverUpdater.App.Tests.ViewModels;

public class DriverRowViewModelTests
{
    [Fact]
    public void Constructor_throws_when_driver_is_null()
    {
        var act = () => new DriverRowViewModel(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Computed_properties_forward_to_driver()
    {
        var driver = NewSampleDriver();

        var row = new DriverRowViewModel(driver);

        row.DeviceName.Should().Be(driver.DeviceName);
        row.Provider.Should().Be(driver.Provider);
        row.Manufacturer.Should().Be(driver.Manufacturer);
        row.Category.Should().Be(driver.Category);
        row.DeviceClass.Should().Be(driver.DeviceClass);
        row.HardwareId.Should().Be(driver.HardwareId);
        row.CurrentVersionText.Should().Be("1.2.3.4");
        row.CurrentDateText.Should().Be("2024-03-06");
        row.IsSigned.Should().Be(driver.IsSigned);
    }

    [Fact]
    public void Current_version_text_is_null_when_driver_has_no_version()
    {
        var driver = DriverInfo.Empty("ROOT\\X");

        var row = new DriverRowViewModel(driver);

        row.CurrentVersionText.Should().BeNull();
        row.CurrentDateText.Should().BeNull();
    }

    [Fact]
    public void Status_defaults_to_unknown_and_can_be_changed()
    {
        var row = new DriverRowViewModel(NewSampleDriver());

        row.Status.Should().Be(DriverStatus.Unknown);

        row.Status = DriverStatus.Outdated;

        row.Status.Should().Be(DriverStatus.Outdated);
    }

    [Fact]
    public void Available_update_setter_exposes_version_date_and_source()
    {
        var row = new DriverRowViewModel(NewSampleDriver());

        row.AvailableVersionText.Should().BeNull();
        row.AvailableDateText.Should().BeNull();
        row.SourceText.Should().BeNull();

        row.AvailableUpdate = new UpdateCandidate(
            ForHardwareId: row.HardwareId,
            Source: UpdateSource.MicrosoftCatalog,
            NewVersion: new Version(2, 0, 0, 0),
            NewDate: new DateOnly(2026, 1, 1),
            DownloadUrl: new Uri("https://example.com/x.cab"),
            SizeBytes: 1024,
            KbArticle: null,
            IsSuperseded: false,
            SourceUpdateId: "abc",
            SupersededIds: Array.Empty<string>());

        row.AvailableVersionText.Should().Be("2.0.0.0");
        row.AvailableDateText.Should().Be("2026-01-01");
        row.SourceText.Should().Be("MicrosoftCatalog");
    }

    [Fact]
    public void Available_update_setter_notifies_computed_update_properties()
    {
        var row = new DriverRowViewModel(NewSampleDriver());
        var notified = new List<string?>();
        row.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        row.AvailableUpdate = new UpdateCandidate(
            ForHardwareId: row.HardwareId,
            Source: UpdateSource.MicrosoftCatalog,
            NewVersion: new Version(2, 0, 0, 0),
            NewDate: new DateOnly(2026, 1, 1),
            DownloadUrl: new Uri("https://example.com/x.cab"),
            SizeBytes: 1024,
            KbArticle: null,
            IsSuperseded: false,
            SourceUpdateId: "abc",
            SupersededIds: Array.Empty<string>());

        notified.Should().Contain(nameof(DriverRowViewModel.AvailableUpdate));
        notified.Should().Contain(nameof(DriverRowViewModel.AvailableVersionText));
        notified.Should().Contain(nameof(DriverRowViewModel.AvailableDateText));
        notified.Should().Contain(nameof(DriverRowViewModel.SourceText));
        notified.Should().Contain(nameof(DriverRowViewModel.UpdateActionText));
        notified.Should().Contain(nameof(DriverRowViewModel.ConfidenceText));
        notified.Should().Contain(nameof(DriverRowViewModel.CanUpdate));
        notified.Should().Contain(nameof(DriverRowViewModel.CanAskAi));
    }

    [Fact]
    public void Ai_recommendation_text_maps_verdict_to_user_facing_guidance()
    {
        var row = new DriverRowViewModel(NewSampleDriver())
        {
            AvailableUpdate = NewCandidate() with
            {
                AiVerification = new AiVerdict(
                    true,
                    AiRiskLevel.Caution,
                    "Use caution",
                    "Reported issues are mixed.",
                    "2.0.0.0",
                    InstalledSuitability: "The installed driver is stable but old.",
                    CandidateSuitability: "The candidate matches this adapter.",
                    RecommendedVersion: "2.0.0.0",
                    AdvisorNote: "Install only if you need the fixes.")
            }
        };

        row.HasAiVerdict.Should().BeTrue();
        row.AiRiskText.Should().Be("Caution");
        row.AiRecommendationText.Should().Be("Use caution");
        row.AiRiskTooltip.Should().Contain("Reported issues are mixed.");
        row.AiRiskTooltip.Should().Contain("The installed driver is stable but old.");
        row.AiRiskTooltip.Should().Contain("Recommended version for this PC: 2.0.0.0");
        row.AiRiskTooltip.Should().Contain("Install only if you need the fixes.");
    }

    [Fact]
    public void CanAskAi_requires_only_that_ai_is_not_currently_checking()
    {
        var row = new DriverRowViewModel(NewSampleDriver());
        row.CanAskAi.Should().BeTrue();

        row.AvailableUpdate = NewCandidate();
        row.CanAskAi.Should().BeTrue();

        row.IsAiChecking = true;
        row.CanAskAi.Should().BeFalse();
    }

    [Fact]
    public void CanUpdate_is_true_for_outdated_updates_and_vendor_page_checks()
    {
        var row = new DriverRowViewModel(NewSampleDriver());
        row.CanUpdate.Should().BeFalse();

        row.AvailableUpdate = new UpdateCandidate(
            ForHardwareId: row.HardwareId,
            Source: UpdateSource.MicrosoftCatalog,
            NewVersion: new Version(2, 0, 0, 0),
            NewDate: new DateOnly(2026, 1, 1),
            DownloadUrl: new Uri("https://example.com/x.cab"),
            SizeBytes: 1024,
            KbArticle: null,
            IsSuperseded: false,
            SourceUpdateId: "abc",
            SupersededIds: Array.Empty<string>());
        row.CanUpdate.Should().BeFalse();

        row.Status = DriverStatus.Outdated;
        row.CanUpdate.Should().BeTrue();

        row.AvailableUpdate = null;
        row.CanUpdate.Should().BeFalse();

        row.Status = DriverStatus.UpToDate;
        row.AvailableUpdate = NewCandidate() with { InstallKind = UpdateInstallKind.VendorPage };
        row.CanUpdate.Should().BeTrue();
    }

    [Fact]
    public void Progress_status_text_shows_download_size_when_total_known()
    {
        var row = new DriverRowViewModel(NewSampleDriver());
        var op = NewActiveOperation() with
        {
            Status = UpdateStatus.Downloading,
            DownloadedBytes = 12_300_000,
            TotalBytes = 380_000_000
        };

        row.ActiveOperation = op;

        row.IsBusy.Should().BeTrue();
        row.IsDownloading.Should().BeTrue();
        row.HasDeterminateProgress.Should().BeTrue();
        row.DownloadPercent.Should().BeApproximately(3.2, 0.2);
        row.ProgressStatusText.Should().Contain("11.7 MB").And.Contain("362.4 MB").And.Contain("3%");
    }

    [Fact]
    public void Progress_status_text_shows_downloaded_only_when_total_unknown()
    {
        var row = new DriverRowViewModel(NewSampleDriver());
        var op = NewActiveOperation() with
        {
            Status = UpdateStatus.Downloading,
            DownloadedBytes = 5 * 1024L * 1024,
            TotalBytes = null
        };

        row.ActiveOperation = op;

        row.HasDeterminateProgress.Should().BeFalse();
        row.ProgressStatusText.Should().Contain("5.0 MB").And.Contain("downloaded");
    }

    [Fact]
    public void Progress_status_text_shows_elapsed_when_installing()
    {
        var row = new DriverRowViewModel(NewSampleDriver());
        var startedAt = DateTimeOffset.UtcNow.AddSeconds(-42);
        row.ActiveOperation = NewActiveOperation() with
        {
            Status = UpdateStatus.Installing,
            InstallStartedAt = startedAt
        };

        row.IsInstalling.Should().BeTrue();
        row.HasDeterminateProgress.Should().BeFalse();
        row.ProgressStatusText.Should().StartWith("Installing...");
        row.ProgressStatusText.Should().MatchRegex(@"\d+:\d{2}");
    }

    [Fact]
    public void Is_busy_resets_to_false_when_active_operation_is_cleared()
    {
        var row = new DriverRowViewModel(NewSampleDriver())
        {
            ActiveOperation = NewActiveOperation() with { Status = UpdateStatus.Downloading }
        };
        row.IsBusy.Should().BeTrue();

        row.ActiveOperation = null;

        row.IsBusy.Should().BeFalse();
        row.IsDownloading.Should().BeFalse();
        row.ProgressStatusText.Should().BeEmpty();
    }

    private static UpdateOperation NewActiveOperation()
    {
        var driver = NewSampleDriver();
        var candidate = new UpdateCandidate(
            ForHardwareId: driver.HardwareId,
            Source: UpdateSource.Oem,
            NewVersion: new Version(2, 0, 0, 0),
            NewDate: new DateOnly(2026, 1, 1),
            DownloadUrl: new Uri("https://example.com/x.exe"),
            SizeBytes: 1024,
            KbArticle: null,
            IsSuperseded: false,
            SourceUpdateId: "vendor-installer:installshield:test:1",
            SupersededIds: Array.Empty<string>());
        return UpdateOperation.NewPending(candidate, driver);
    }

    private static UpdateCandidate NewCandidate() =>
        new(
            ForHardwareId: "PCI\\VEN_8086&DEV_1234",
            Source: UpdateSource.MicrosoftCatalog,
            NewVersion: new Version(2, 0, 0, 0),
            NewDate: new DateOnly(2026, 1, 1),
            DownloadUrl: new Uri("https://example.com/x.cab"),
            SizeBytes: 1024,
            KbArticle: null,
            IsSuperseded: false,
            SourceUpdateId: "abc",
            SupersededIds: Array.Empty<string>());

    [Fact]
    public void Status_change_notifies_can_update()
    {
        var row = new DriverRowViewModel(NewSampleDriver());
        var notified = new List<string?>();
        row.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        row.Status = DriverStatus.Outdated;

        notified.Should().Contain(nameof(DriverRowViewModel.Status));
        notified.Should().Contain(nameof(DriverRowViewModel.CanUpdate));
    }

    private static DriverInfo NewSampleDriver() => new(
        DeviceId: "PCI\\VEN_8086&DEV_1234\\3&1&0",
        HardwareId: "PCI\\VEN_8086&DEV_1234",
        DeviceName: "Sample Adapter",
        Category: DriverCategory.Network,
        Provider: "Intel",
        Manufacturer: "Intel Corporation",
        CurrentVersion: new Version(1, 2, 3, 4),
        CurrentDate: new DateOnly(2024, 3, 6),
        InfName: "oem1.inf",
        InfPath: "C:\\Windows\\INF\\oem1.inf",
        IsSigned: true,
        DeviceClass: "Net");
}
