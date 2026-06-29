using DriverUpdater.Core.Models;

namespace DriverUpdater.Services.Scanning;

// Shared driver/candidate matching logic used by both the interactive scan
// (MainViewModel) and the headless scheduled scan (ScheduledScanRunner) so the
// two paths agree on which candidate binds to which device and which of several
// competing candidates wins.
public static class DriverUpdateMatcher
{
    // True when one of the IDs is a clean prefix of the other, where "clean" means the
    // next character after the prefix is a Windows hardware-ID separator (\ or &). Without
    // that boundary, IDs like ROOT\X and ROOT\XYZ would match each other coincidentally,
    // which has caused cross-vendor confusion (an AMD chipset candidate landing on a
    // Logitech row, for example).
    public static bool IsBoundaryPrefix(string a, string b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        if (a.Length == b.Length)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
        var (shorter, longer) = a.Length < b.Length ? (a, b) : (b, a);
        if (!longer.StartsWith(shorter, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var nextChar = longer[shorter.Length];
        return nextChar == '\\' || nextChar == '&';
    }

    // Decides whether a newly discovered candidate should replace the one already bound to
    // a device. A confirmed update always beats a vendor advisory; between two of the same
    // confidence the newer version (then the newer date) wins.
    public static bool ShouldReplace(UpdateCandidate? current, UpdateCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        if (current is null)
        {
            return true;
        }

        var currentPriority = CandidatePriority(current);
        var newPriority = CandidatePriority(candidate);
        if (newPriority < currentPriority)
        {
            return false;
        }
        if (newPriority > currentPriority)
        {
            return true;
        }

        var versionComparison = candidate.NewVersion.CompareTo(current.NewVersion);
        if (versionComparison != 0)
        {
            return versionComparison > 0;
        }

        return candidate.NewDate > current.NewDate;
    }

    private static int CandidatePriority(UpdateCandidate candidate) =>
        candidate.Confidence == UpdateConfidence.Confirmed ? 2 : 1;
}
