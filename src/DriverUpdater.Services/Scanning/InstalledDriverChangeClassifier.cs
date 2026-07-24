using DriverUpdater.Core.Models;

namespace DriverUpdater.Services.Scanning;

internal static class InstalledDriverChangeClassifier
{
    internal static bool IsUpgrade(DriverInfo before, InstalledDriverState current)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(current);

        var versionComparison = before.CurrentVersion is not null && current.Version is not null
            ? current.Version.CompareTo(before.CurrentVersion)
            : (int?)null;
        var dateComparison = before.CurrentDate is not null && current.Date is not null
            ? current.Date.Value.CompareTo(before.CurrentDate.Value)
            : (int?)null;

        // Driver versions are the primary ordering signal. Vendors sometimes publish a
        // higher version with an older INF date, so rejecting that combination produces a
        // false downgrade result. Only use the date to order drivers when their versions
        // are equal or one side has no readable version.
        if (versionComparison is > 0)
        {
            return true;
        }

        if (versionComparison is < 0)
        {
            return false;
        }

        if (versionComparison is null && dateComparison is not null and not 0)
        {
            return dateComparison > 0;
        }

        return dateComparison > 0
            || before.CurrentVersion is null && current.Version is not null
            || before.CurrentDate is null && current.Date is not null;
    }
}
