using DriverUpdater.Core.Models;

namespace DriverUpdater.App.ViewModels;

public sealed class UpdateSummaryViewModel
{
    public UpdateSummaryViewModel(UpdateVerificationReport report, AppLanguage language)
    {
        ArgumentNullException.ThrowIfNull(report);
        var hebrew = language == AppLanguage.Hebrew;
        WindowTitle = hebrew ? "DriverUpdater - סיכום עדכון" : "DriverUpdater - Update summary";
        Header = report.IsAfterRestart
            ? hebrew ? "בדיקת העדכונים לאחר ההפעלה מחדש" : "Update check after restart"
            : hebrew ? "העדכון הסתיים ונבדק" : "Updates finished and checked";
        Subheader = hebrew
            ? "האפליקציה בדקה מול Windows איזה דרייבר פעיל בפועל."
            : "The app checked Windows to see which driver is actually active.";
        AiLabel = report.AiWasUsed
            ? hebrew ? "הסבר פשוט של AI" : "Plain-language AI explanation"
            : hebrew ? "AI אינו מוגדר או לא היה זמין, מוצג סיכום מקומי" : "AI is not configured or was unavailable, showing a local summary";
        SummaryText = string.IsNullOrWhiteSpace(report.AiSummary)
            ? BuildFallbackSummary(report, hebrew)
            : report.AiSummary.Trim();
        ResultsHeader = hebrew ? "מה קרה לכל מכשיר" : "What happened to each device";
        VerifiedCountText = hebrew
            ? $"{report.VerifiedCount} עודכנו ואומתו"
            : $"{report.VerifiedCount} updated and verified";
        PendingCountText = hebrew
            ? $"{report.PendingRestartCount} ממתינים להפעלה מחדש"
            : $"{report.PendingRestartCount} waiting for restart";
        AttentionCountText = hebrew
            ? $"{report.AttentionCount} דורשים תשומת לב"
            : $"{report.AttentionCount} need attention";
        CopyButtonText = hebrew ? "העתק סיכום" : "Copy summary";
        CloseButtonText = hebrew ? "סגור" : "Close";
        Items = report.Items.Select(i => new UpdateSummaryItemViewModel(i, language)).ToArray();
        CopyText = BuildCopyText();
    }

    public string WindowTitle { get; }
    public string Header { get; }
    public string Subheader { get; }
    public string AiLabel { get; }
    public string SummaryText { get; }
    public string ResultsHeader { get; }
    public string VerifiedCountText { get; }
    public string PendingCountText { get; }
    public string AttentionCountText { get; }
    public string CopyButtonText { get; }
    public string CloseButtonText { get; }
    public IReadOnlyList<UpdateSummaryItemViewModel> Items { get; }
    public string CopyText { get; }

    private static string BuildFallbackSummary(UpdateVerificationReport report, bool hebrew)
    {
        if (hebrew)
        {
            return report.PendingRestartCount > 0
                ? $"Windows אישר ש־{report.VerifiedCount} עדכונים כבר פעילים. {report.PendingRestartCount} עדכונים נוספים ייבדקו לאחר הפעלה מחדש של המחשב."
                : report.AttentionCount > 0
                    ? $"Windows אישר ש־{report.VerifiedCount} עדכונים הצליחו. {report.AttentionCount} פריטים לא עודכנו או לא ניתנים לאימות ודורשים בדיקה."
                    : $"Windows אישר שכל {report.VerifiedCount} העדכונים הותקנו והם פעילים.";
        }

        return report.PendingRestartCount > 0
            ? $"Windows confirmed that {report.VerifiedCount} updates are already active. Another {report.PendingRestartCount} will be checked after the computer restarts."
            : report.AttentionCount > 0
                ? $"Windows confirmed that {report.VerifiedCount} updates succeeded. {report.AttentionCount} items were not updated or could not be verified and need attention."
                : $"Windows confirmed that all {report.VerifiedCount} updates are installed and active.";
    }

    private string BuildCopyText()
    {
        var lines = new List<string> { Header, SummaryText, string.Empty };
        foreach (var item in Items)
        {
            lines.Add($"{item.DeviceName}: {item.StatusText}");
            lines.Add(item.Explanation);
            lines.Add(item.VersionText);
            lines.Add(string.Empty);
        }
        return string.Join(Environment.NewLine, lines).TrimEnd();
    }
}
