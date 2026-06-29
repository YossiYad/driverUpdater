using DriverUpdater.Core.Models;
using DriverUpdater.Services.Scanning;
using FluentAssertions;

namespace DriverUpdater.Services.Tests.Scanning;

public class DriverUpdateMatcherTests
{
    [Theory]
    [InlineData(@"PCI\VEN_1002&DEV_747E", @"PCI\VEN_1002&DEV_747E", true)]
    [InlineData(@"PCI\VEN_1002&DEV_747E", @"PCI\VEN_1002&DEV_747E&SUBSYS_24141458", true)]
    [InlineData(@"USB\VID_046D", @"USB\VID_046D&PID_0001", true)]
    [InlineData(@"ROOT\X", @"ROOT\X\0001", true)]
    [InlineData(@"ROOT\X", @"ROOT\XYZ", false)]
    [InlineData(@"PCI\VEN_10", @"PCI\VEN_1002", false)]
    public void IsBoundaryPrefix_matches_only_on_separator_boundary(string a, string b, bool expected)
    {
        DriverUpdateMatcher.IsBoundaryPrefix(a, b).Should().Be(expected);
    }

    [Fact]
    public void ShouldReplace_accepts_any_candidate_when_none_is_bound_yet()
    {
        DriverUpdateMatcher.ShouldReplace(null, Candidate(new Version(1, 0))).Should().BeTrue();
    }

    [Fact]
    public void ShouldReplace_keeps_confirmed_update_over_vendor_advisory()
    {
        var confirmed = Candidate(new Version(2, 0), confidence: UpdateConfidence.Confirmed);
        var advisory = Candidate(new Version(2026, 5, 28, 0), confidence: UpdateConfidence.Advisory);

        DriverUpdateMatcher.ShouldReplace(confirmed, advisory).Should().BeFalse();
    }

    [Fact]
    public void ShouldReplace_upgrades_vendor_advisory_to_confirmed_update()
    {
        var advisory = Candidate(new Version(2026, 5, 28, 0), confidence: UpdateConfidence.Advisory);
        var confirmed = Candidate(new Version(2, 0), confidence: UpdateConfidence.Confirmed);

        DriverUpdateMatcher.ShouldReplace(advisory, confirmed).Should().BeTrue();
    }

    [Fact]
    public void ShouldReplace_prefers_the_newer_version_at_equal_confidence()
    {
        var current = Candidate(new Version(1, 0));
        var newer = Candidate(new Version(2, 0));
        var older = Candidate(new Version(0, 9));

        DriverUpdateMatcher.ShouldReplace(current, newer).Should().BeTrue();
        DriverUpdateMatcher.ShouldReplace(current, older).Should().BeFalse();
    }

    [Fact]
    public void ShouldReplace_breaks_version_ties_on_date()
    {
        var current = Candidate(new Version(1, 0), date: new DateOnly(2026, 1, 1));
        var newerDate = Candidate(new Version(1, 0), date: new DateOnly(2026, 6, 1));

        DriverUpdateMatcher.ShouldReplace(current, newerDate).Should().BeTrue();
    }

    private static UpdateCandidate Candidate(
        Version version,
        DateOnly? date = null,
        UpdateConfidence confidence = UpdateConfidence.Confirmed) => new(
        ForHardwareId: "HW\\X",
        Source: UpdateSource.WindowsUpdate,
        NewVersion: version,
        NewDate: date ?? new DateOnly(2026, 1, 1),
        DownloadUrl: new Uri("https://example.com/x.cab"),
        SizeBytes: 1024,
        KbArticle: null,
        IsSuperseded: false,
        SourceUpdateId: Guid.NewGuid().ToString(),
        SupersededIds: Array.Empty<string>(),
        Confidence: confidence);
}
