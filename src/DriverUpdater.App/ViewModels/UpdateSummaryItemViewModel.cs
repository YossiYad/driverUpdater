using DriverUpdater.Core.Models;

namespace DriverUpdater.App.ViewModels;

public sealed class UpdateSummaryItemViewModel
{
    public UpdateSummaryItemViewModel(UpdateVerificationItem item, AppLanguage language)
    {
        ArgumentNullException.ThrowIfNull(item);
        DeviceName = item.DeviceName;
        CategoryText = FriendlyCategory(item.Category, language);
        StatusText = FriendlyStatus(item.Status, language);
        Explanation = FriendlyExplanation(item, language);
        VersionText = BuildVersionText(item, language);
        StatusColor = item.Status switch
        {
            UpdateVerificationStatus.VerifiedUpdated => "#FF2E7D32",
            UpdateVerificationStatus.PendingRestart => "#FF9A6700",
            UpdateVerificationStatus.ManualActionRequired => "#FF2563EB",
            UpdateVerificationStatus.Skipped => "#FF6B7280",
            UpdateVerificationStatus.Inconclusive => "#FF9A6700",
            _ => "#FFB42318"
        };
        StatusIcon = item.Status switch
        {
            UpdateVerificationStatus.VerifiedUpdated => "✓",
            UpdateVerificationStatus.PendingRestart => "↻",
            UpdateVerificationStatus.ManualActionRequired => "↗",
            UpdateVerificationStatus.Skipped => "○",
            UpdateVerificationStatus.Inconclusive => "?",
            _ => "!"
        };
    }

    public string DeviceName { get; }
    public string CategoryText { get; }
    public string StatusText { get; }
    public string Explanation { get; }
    public string VersionText { get; }
    public string StatusColor { get; }
    public string StatusIcon { get; }

    private static string FriendlyStatus(UpdateVerificationStatus status, AppLanguage language) =>
        language == AppLanguage.Hebrew
            ? status switch
            {
                UpdateVerificationStatus.VerifiedUpdated => "עודכן בהצלחה",
                UpdateVerificationStatus.PendingRestart => "ממתין להפעלה מחדש",
                UpdateVerificationStatus.NotUpdated => "לא עודכן",
                UpdateVerificationStatus.Failed => "העדכון נכשל",
                UpdateVerificationStatus.Skipped => "לא בוצע",
                UpdateVerificationStatus.ManualActionRequired => "המשך באתר היצרן",
                _ => "לא ניתן לוודא"
            }
            : status switch
            {
                UpdateVerificationStatus.VerifiedUpdated => "Updated successfully",
                UpdateVerificationStatus.PendingRestart => "Waiting for restart",
                UpdateVerificationStatus.NotUpdated => "Not updated",
                UpdateVerificationStatus.Failed => "Update failed",
                UpdateVerificationStatus.Skipped => "Not installed",
                UpdateVerificationStatus.ManualActionRequired => "Continue on vendor website",
                _ => "Could not verify"
            };

    private static string FriendlyExplanation(UpdateVerificationItem item, AppLanguage language) =>
        language == AppLanguage.Hebrew
            ? item.Status switch
            {
                UpdateVerificationStatus.VerifiedUpdated => "Windows אישר שהמכשיר משתמש כעת בדרייבר החדש.",
                UpdateVerificationStatus.PendingRestart => "העדכון הותקן, אך הוא ייכנס לפעולה רק לאחר הפעלה מחדש של המחשב.",
                UpdateVerificationStatus.NotUpdated => "ההתקנה הסתיימה, אך רכיב זה לא השתנה. ייתכן שהוא כבר היה מעודכן.",
                UpdateVerificationStatus.Failed => item.CurrentVersion is not null || item.CurrentDate is not null
                    ? "המתקין דיווח על כשל, ו־Windows אישר שהדרייבר הקודם עדיין פעיל."
                    : "המתקין דיווח על כשל, והאפליקציה לא הצליחה לאמת איזו גרסה פעילה כעת.",
                UpdateVerificationStatus.Skipped => "העדכון לא הותקן ולא נעשה שינוי בדרייבר.",
                UpdateVerificationStatus.ManualActionRequired => "לא נמצא קובץ התקנה אוטומטי בטוח. דף היצרן נפתח לבדיקה או להתקנה ידנית.",
                _ => "האפליקציה לא הצליחה לקרוא מ־Windows איזה דרייבר פעיל כעת. מומלץ לבצע סריקה נוספת."
            }
            : item.Status switch
            {
                UpdateVerificationStatus.VerifiedUpdated => "Windows confirmed that the device is now using the new driver.",
                UpdateVerificationStatus.PendingRestart => "The update is installed, but it will only become active after the computer restarts.",
                UpdateVerificationStatus.NotUpdated => "The installer completed, but this component did not change. It may already have been current.",
                UpdateVerificationStatus.Failed => item.CurrentVersion is not null || item.CurrentDate is not null
                    ? "The installer reported a failure, and Windows confirmed that the previous driver is still active."
                    : "The installer reported a failure, and the app could not verify which version is active now.",
                UpdateVerificationStatus.Skipped => "The update was not installed and the driver was not changed.",
                UpdateVerificationStatus.ManualActionRequired => "No safe automatic installer was found. The vendor page was opened for a manual check or installation.",
                _ => "The app could not read which driver Windows is using now. Run another scan to check again."
            };

    private static string FriendlyCategory(DriverCategory category, AppLanguage language)
    {
        if (language != AppLanguage.Hebrew)
        {
            return category switch
            {
                DriverCategory.Display => "Screen and graphics",
                DriverCategory.Audio => "Sound",
                DriverCategory.Network => "Network and internet",
                DriverCategory.Storage => "Storage",
                DriverCategory.Input or DriverCategory.HumanInterface => "Keyboard, mouse, or input",
                DriverCategory.Bluetooth => "Bluetooth",
                DriverCategory.Camera => "Camera",
                DriverCategory.Printer => "Printer",
                DriverCategory.Firmware => "Device firmware",
                DriverCategory.Usb => "USB device",
                _ => "System device"
            };
        }

        return category switch
        {
            DriverCategory.Display => "מסך וגרפיקה",
            DriverCategory.Audio => "שמע",
            DriverCategory.Network => "רשת ואינטרנט",
            DriverCategory.Storage => "אחסון",
            DriverCategory.Input or DriverCategory.HumanInterface => "מקלדת, עכבר או אמצעי קלט",
            DriverCategory.Bluetooth => "Bluetooth",
            DriverCategory.Camera => "מצלמה",
            DriverCategory.Printer => "מדפסת",
            DriverCategory.Firmware => "תוכנת התקן פנימית",
            DriverCategory.Usb => "התקן USB",
            _ => "התקן מערכת"
        };
    }

    private static string BuildVersionText(UpdateVerificationItem item, AppLanguage language)
    {
        var before = item.PreviousVersion?.ToString() ?? "?";
        if (item.Status == UpdateVerificationStatus.ManualActionRequired)
        {
            return language == AppLanguage.Hebrew
                ? $"הגרסה המותקנת כעת: {before}  |  לא בוצע שינוי אוטומטי"
                : $"Currently installed version: {before}  |  No automatic change was made";
        }
        var current = item.CurrentVersion?.ToString()
            ?? (item.Status == UpdateVerificationStatus.PendingRestart ? item.ExpectedVersion?.ToString() : null)
            ?? "?";
        return language == AppLanguage.Hebrew
            ? $"גרסה קודמת: {before}  |  גרסה לאחר העדכון: {current}"
            : $"Previous version: {before}  |  Version after update: {current}";
    }
}
