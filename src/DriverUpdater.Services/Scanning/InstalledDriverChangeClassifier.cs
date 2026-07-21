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

        // Conflicting or older metadata is not proof of a successful update. This avoids
        // reporting an accidental downgrade as verified merely because the value changed.
        if (versionComparison < 0 || dateComparison < 0)
        {
            return false;
        }

        return versionComparison > 0
            || dateComparison > 0
            || before.CurrentVersion is null && current.Version is not null
            || before.CurrentDate is null && current.Date is not null;
    }
}
