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
        ManualActionCountText = hebrew
            ? $"{report.ManualActionCount} להמשך באתר היצרן"
            : $"{report.ManualActionCount} continue on vendor website";
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
    public string ManualActionCountText { get; }
    public string AttentionCountText { get; }
    public string CopyButtonText { get; }
    public string CloseButtonText { get; }
    public IReadOnlyList<UpdateSummaryItemViewModel> Items { get; }
    public string CopyText { get; }

    private static string BuildFallbackSummary(UpdateVerificationReport report, bool hebrew)
    {
        var parts = new List<string>();
        if (report.VerifiedCount > 0)
        {
            parts.Add(hebrew
                ? $"Windows אישר ש־{report.VerifiedCount} עדכונים פעילים."
                : $"Windows confirmed that {report.VerifiedCount} updates are active.");
        }
        if (report.PendingRestartCount > 0)
        {
            parts.Add(hebrew
                ? $"{report.PendingRestartCount} עדכונים ייבדקו לאחר הפעלה מחדש של המחשב."
                : $"{report.PendingRestartCount} updates will be checked after the computer restarts.");
        }
        if (report.AttentionCount > 0)
        {
            parts.Add(hebrew
                ? $"{report.AttentionCount} פריטים לא השתנו או לא ניתנים לאימות ודורשים בדיקה."
                : $"{report.AttentionCount} items did not change or could not be verified and need attention.");
        }
        if (report.ManualActionCount > 0)
        {
            parts.Add(hebrew
                ? $"עבור {report.ManualActionCount} פריטים לא בוצעה התקנה אוטומטית, ונפתח דף היצרן להמשך ידני."
                : $"No automatic installation was attempted for {report.ManualActionCount} items, and their vendor pages were opened for manual follow-up.");
        }
        if (parts.Count == 0)
        {
            parts.Add(hebrew ? "לא נדרש שינוי בדרייברים." : "No driver changes were required.");
        }
        return string.Join(' ', parts);
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
