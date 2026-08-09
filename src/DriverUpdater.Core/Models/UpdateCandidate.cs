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
    AiVerdict? AiVerification = null,
    UpdateRebootBehavior RebootBehavior = UpdateRebootBehavior.Unknown,
    string? VersionLabel = null,
    string? InstalledVersionLabel = null)
{
    // The version a human should see. Several vendors brand their release differently from the
    // driver version Windows ends up reporting (NVIDIA ships "610.88" as INF 32.0.16.1088), and
    // sources that only know a release date synthesise NewVersion from it purely so candidates
    // stay comparable. NewVersion therefore stays the comparison key and this stays the label.
    public string DisplayVersion =>
        string.IsNullOrWhiteSpace(VersionLabel) ? NewVersion.ToString() : VersionLabel;

    public bool IsNewerThan(DriverInfo current)
    {
        ArgumentNullException.ThrowIfNull(current);

        // Never downgrade a Windows inbox driver (10.0.<osbuild>.x, e.g. 10.0.26100.8521) to an
        // OEM catalog driver that uses a calendar-year version (YYYY.MM.DD.x, e.g. 2018.5.31.0).
        // This MUST be decided before any date/number comparison below: inbox drivers report a
        // placeholder date (commonly 2006-06-21), so the date branch would see a 2018 package as
        // "newer than 2006" and the raw "2018 > 10" number comparison is also a false positive.
        // The modern build number (26100) is the real signal. Because Windows silently rejects
        // the downgrade of a protected inbox driver, such an "update" otherwise reappears on
        // every scan and is installed in a loop.
        if (current.CurrentVersion is { } installed
            && IsWindowsInboxVersion(installed)
            && IsCalendarVersion(NewVersion))
        {
            return false;
        }

        // Generic drivers that ship in the Windows image carry a placeholder release date
        // instead of a real one, so their date says nothing about how current they are. It
        // must not be used as the comparison key: WinUSB reports 1.1.0.0 with the placeholder
        // date, and a calendar-versioned catalog package (2021.12.29.0) then looks like an
        // upgrade purely because 2021 comes after the placeholder year. Windows refuses to
        // bind that package, so the "update" is reinstalled on every scan.
        var installedDate = IsInboxPlaceholderDate(current.CurrentDate) ? null : current.CurrentDate;

        // Version formats are not comparable across every driver family. A package can
        // report a high Windows build number while the installed component uses its own
        // product version. An explicitly older driver date is reliable evidence that the
        // package is not an upgrade, regardless of the numeric version format.
        if (installedDate is { } olderThanCandidate
            && NewDate != DateOnly.MinValue
            && NewDate < olderThanCandidate)
        {
            return false;
        }

        // Most reliable comparison: both the candidate and the installed driver expose a date.
        if (IsDateBasedVersion(NewVersion, NewDate) && installedDate is { } currentDate)
        {
            return NewDate > currentDate;
        }

        if (current.CurrentVersion is null)
        {
            return true;
        }

        // Calendar-year candidate vs a low-major classic driver (e.g. Realtek 6.0.9927.1 or
        // an Intel NIC 12.19.0.11) with no usable date to compare against: still incomparable
        // schemes ("2021 > 6"), so refuse rather than downgrade.
        if (IsCalendarVersion(NewVersion)
            && installedDate is null
            && current.CurrentVersion.Major < 100)
        {
            return false;
        }

        return NewVersion > current.CurrentVersion;
    }

    // The date Windows stamps on the generic drivers bundled with the OS image. It is a
    // constant, not a release date, and every inbox driver on every machine reports it.
    private static readonly DateOnly InboxPlaceholderDate = new(2006, 6, 21);

    private static bool IsInboxPlaceholderDate(DateOnly? date) => date == InboxPlaceholderDate;

    private static bool IsDateBasedVersion(Version version, DateOnly date) =>
        version.Major == date.Year
        && version.Minor == date.Month
        && version.Build == date.Day
        && version.Revision is 0 or -1;

    // A version whose components look like a calendar date (YYYY.MM.DD), independent of any
    // supplied release date. OEM catalog drivers commonly encode their release date this way.
    private static bool IsCalendarVersion(Version version) =>
        version.Major is >= 2000 and <= 2100
        && version.Minor is >= 1 and <= 12
        && version.Build is >= 1 and <= 31;

    // A Windows inbox driver: kernel-style version 10.0.<osbuild>.<revision> where the build
    // field carries the OS build number (Win10/11 builds are >= 10240). These ship with
    // Windows and must not be "downgraded" to a calendar-versioned OEM package.
    private static bool IsWindowsInboxVersion(Version version) =>
        version.Major == 10
        && version.Minor == 0
        && version.Build >= 10000;
}
