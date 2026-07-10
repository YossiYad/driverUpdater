namespace DriverUpdater.Core.Models;

public sealed record UpdateCandidate(
    string ForHardwareId,
    UpdateSource Source,
    Version NewVersion,
    DateOnly NewDate,
    Uri DownloadUrl,
    long SizeBytes,
    string? KbArticle,
    bool IsSuperseded,
    string SourceUpdateId,
    IReadOnlyList<string> SupersededIds,
    UpdateInstallKind InstallKind = UpdateInstallKind.WindowsUpdate,
    UpdateConfidence Confidence = UpdateConfidence.Confirmed,
    AiVerdict? AiVerification = null)
{
    public bool IsNewerThan(DriverInfo current)
    {
        ArgumentNullException.ThrowIfNull(current);

        if (IsDateBasedVersion(NewVersion, NewDate) && current.CurrentDate is { } currentDate)
        {
            return NewDate > currentDate;
        }

        if (current.CurrentVersion is null)
        {
            return true;
        }

        // When a catalog entry IS date-based (NewVersion encodes YYYY.MM.DD.0) but the
        // installed driver has no CurrentDate to compare against, we fall through to the
        // version number comparison below. That comparison is wrong when the installed
        // driver uses a Windows build version (major ≤ 99, e.g. 10.0.26100.1882 for an
        // inbox driver or 12.19.0.11 for an Intel NIC): "2021 > 10" is numerically true
        // but would downgrade a current Windows 11 inbox driver to a 2021 OEM package.
        // Treat such mismatched schemes as incomparable — refuse the update.
        if (IsDateBasedVersion(NewVersion, NewDate)
            && current.CurrentDate is null
            && current.CurrentVersion.Major < 100)
        {
            return false;
        }

        return NewVersion > current.CurrentVersion;
    }

    private static bool IsDateBasedVersion(Version version, DateOnly date) =>
        version.Major == date.Year
        && version.Minor == date.Month
        && version.Build == date.Day
        && version.Revision is 0 or -1;
}
