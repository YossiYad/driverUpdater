using DriverUpdater.Core.Models;
using FluentAssertions;

namespace DriverUpdater.Core.Tests.Models;

public class UpdateCandidateTests
{
    [Fact]
    public void IsNewerThan_returns_true_when_current_version_is_null()
    {
        var candidate = NewCandidate(new Version(1, 0, 0, 0));
        var current = SampleDriver(currentVersion: null);

        candidate.IsNewerThan(current).Should().BeTrue();
    }

    [Fact]
    public void IsNewerThan_returns_true_when_candidate_version_is_higher()
    {
        var candidate = NewCandidate(new Version(2, 1, 0, 0));
        var current = SampleDriver(currentVersion: new Version(2, 0, 9, 9));

        candidate.IsNewerThan(current).Should().BeTrue();
    }

    [Fact]
    public void IsNewerThan_returns_false_when_versions_are_equal()
    {
        var candidate = NewCandidate(new Version(2, 0, 0, 0));
        var current = SampleDriver(currentVersion: new Version(2, 0, 0, 0));

        candidate.IsNewerThan(current).Should().BeFalse();
    }

    [Fact]
    public void IsNewerThan_returns_false_when_candidate_version_is_older()
    {
        var candidate = NewCandidate(new Version(1, 0, 0, 0));
        var current = SampleDriver(currentVersion: new Version(2, 0, 0, 0));

        candidate.IsNewerThan(current).Should().BeFalse();
    }

    [Fact]
    public void IsNewerThan_compares_date_based_candidate_to_current_driver_date()
    {
        var candidate = NewCandidate(new Version(2026, 5, 14, 0), new DateOnly(2026, 5, 14));
        var current = SampleDriver(new Version(32, 0, 23027, 2005)) with
        {
            CurrentDate = new DateOnly(2026, 2, 17)
        };

        candidate.IsNewerThan(current).Should().BeTrue();
    }

    [Fact]
    public void IsNewerThan_rejects_date_based_candidate_when_current_driver_date_is_newer()
    {
        var candidate = NewCandidate(new Version(2026, 5, 14, 0), new DateOnly(2026, 5, 14));
        var current = SampleDriver(new Version(32, 0, 23027, 2005)) with
        {
            CurrentDate = new DateOnly(2026, 6, 1)
        };

        candidate.IsNewerThan(current).Should().BeFalse();
    }

    [Fact]
    public void IsNewerThan_throws_when_current_is_null()
    {
        var candidate = NewCandidate(new Version(1, 0, 0, 0));

        var act = () => candidate.IsNewerThan(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    private static UpdateCandidate NewCandidate(Version newVersion, DateOnly? newDate = null) => new(
        ForHardwareId: "PCI\\VEN_8086&DEV_1234",
        Source: UpdateSource.WindowsUpdate,
        NewVersion: newVersion,
        NewDate: newDate ?? new DateOnly(2026, 1, 1),
        DownloadUrl: new Uri("https://download.windowsupdate.com/example.cab"),
        SizeBytes: 1_234_567,
        KbArticle: "KB1234567",
        IsSuperseded: false,
        SourceUpdateId: Guid.NewGuid().ToString(),
        SupersededIds: Array.Empty<string>());

    private static DriverInfo SampleDriver(Version? currentVersion) => new(
        DeviceId: "PCI\\VEN_8086&DEV_1234",
        HardwareId: "PCI\\VEN_8086&DEV_1234&REV_01",
        DeviceName: "Sample",
        Category: DriverCategory.Display,
        Provider: "Intel",
        Manufacturer: "Intel",
        CurrentVersion: currentVersion,
        CurrentDate: new DateOnly(2024, 1, 1),
        InfName: "oem.inf",
        InfPath: "C:\\Windows\\INF\\oem.inf",
        IsSigned: true,
        DeviceClass: "Display");
}
