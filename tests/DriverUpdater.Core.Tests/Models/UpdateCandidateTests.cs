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

    // A genuinely date-versioned candidate (NewVersion encodes YYYY.MM.DD.0 matching NewDate)
    // must NOT be considered newer than a Windows inbox or classic driver when the installed
    // driver has no CurrentDate — because "2021 > 10" is numerically true but means a downgrade.
    // The guard only fires when IsDateBasedVersion is true (NewVersion matches NewDate exactly).
    [Theory]
    [InlineData("2021.12.5.0",  2021, 12, 5,  "10.0.26100.1882")]   // Generic PnP Monitor / WAN Miniport
    [InlineData("2018.7.17.0",  2018, 7,  17, "10.0.26100.1882")]   // Intel Processor
    [InlineData("2018.5.31.0",  2018, 5,  31, "10.0.19041.3636")]   // WAN Miniport older build
    [InlineData("2021.12.5.0",  2021, 12, 5,  "6.0.9927.1")]         // vs Realtek-style version
    [InlineData("2024.1.1.0",   2024, 1,  1,  "12.19.0.11")]          // vs Intel NIC-style version
    public void IsNewerThan_returns_false_for_genuine_date_candidate_against_low_major_driver_without_date(
        string candidateVersion, int year, int month, int day, string installedVersion)
    {
        var candidate = NewCandidate(Version.Parse(candidateVersion), new DateOnly(year, month, day));
        var current = SampleDriver(Version.Parse(installedVersion)) with { CurrentDate = null };

        candidate.IsNewerThan(current).Should().BeFalse();
    }

    [Fact]
    public void IsNewerThan_still_compares_two_genuine_date_year_versions_normally()
    {
        // Both sides are date-year versioned (NewDate matches NewVersion) — newer wins.
        var candidate = NewCandidate(new Version(2024, 3, 15, 0), new DateOnly(2024, 3, 15));
        var current = SampleDriver(new Version(2022, 11, 1, 0)) with { CurrentDate = null };

        candidate.IsNewerThan(current).Should().BeTrue();
    }

    [Fact]
    public void IsNewerThan_uses_date_comparison_when_current_has_date_even_if_version_schemes_differ()
    {
        // When CurrentDate is set the existing date path handles it; the guard must not interfere.
        var candidate = NewCandidate(new Version(2021, 12, 5, 0), new DateOnly(2021, 12, 5));
        var current = SampleDriver(new Version(10, 0, 26100, 1882)) with { CurrentDate = new DateOnly(2023, 1, 1) };

        candidate.IsNewerThan(current).Should().BeFalse(); // 2021-12-05 < 2023-01-01
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
