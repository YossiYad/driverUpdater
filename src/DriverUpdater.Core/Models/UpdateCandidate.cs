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
    UpdateConfidence Confidence = UpdateConfidence.Confirmed)
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
        return NewVersion > current.CurrentVersion;
    }

    private static bool IsDateBasedVersion(Version version, DateOnly date) =>
        version.Major == date.Year
        && version.Minor == date.Month
        && version.Build == date.Day
        && version.Revision is 0 or -1;
}
