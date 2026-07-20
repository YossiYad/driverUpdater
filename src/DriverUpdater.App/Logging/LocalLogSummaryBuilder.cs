using System.Text;

namespace DriverUpdater.App.Logging;

public static class LocalLogSummaryBuilder
{
    private const int MaxDetailLength = 1800;

    public static string Build(IReadOnlyList<LogEntry> entries, string aiFailureReason)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentException.ThrowIfNullOrWhiteSpace(aiFailureReason);

        var errors = entries.Count(entry => IsAtLeast(entry.Level, "Error"));
        var warnings = entries.Count(entry =>
            string.Equals(entry.Level, "Warning", StringComparison.OrdinalIgnoreCase));
        var important = entries
            .Where(entry => IsAtLeast(entry.Level, "Warning"))
            .Reverse()
            .DistinctBy(entry => entry.Category + "\n" + entry.Message)
            .Take(8)
            .Reverse()
            .ToArray();
        var updateSummary = entries.LastOrDefault(entry =>
            entry.Message.Contains("Update run summary", StringComparison.OrdinalIgnoreCase));
        var scanSummary = entries.LastOrDefault(entry =>
            entry.Message.Contains("Scan result summary", StringComparison.OrdinalIgnoreCase));

        var text = new StringBuilder();
        text.AppendLine("Local diagnostic summary (AI unavailable)");
        text.Append("AI status: ").AppendLine(aiFailureReason.Trim());
        text.Append("Session: ").Append(entries.Count).Append(" log entries, ")
            .Append(errors).Append(" errors, and ").Append(warnings).AppendLine(" warnings.");

        AppendStructuredBlock(text, "Latest update run", updateSummary);
        AppendStructuredBlock(text, "Latest driver scan", scanSummary);

        if (important.Length > 0)
        {
            text.AppendLine("Recent warnings and errors:");
            foreach (var entry in important)
            {
                text.Append("- ").Append(entry.Timestamp.ToString("HH:mm:ss"))
                    .Append(" [").Append(entry.Level).Append("] ");
                if (!string.IsNullOrWhiteSpace(entry.Category))
                {
                    text.Append(entry.Category).Append(": ");
                }
                text.AppendLine(Truncate(CollapseWhitespace(entry.Message), 500));
            }
        }
        else
        {
            text.AppendLine("No warning or error entries were recorded in the current log buffer.");
        }

        text.AppendLine("Next step: share this summary together with the original log file when reporting a problem.");
        return text.ToString().TrimEnd();
    }

    private static void AppendStructuredBlock(StringBuilder text, string title, LogEntry? entry)
    {
        if (entry is null)
        {
            return;
        }

        text.Append(title).AppendLine(":");
        text.AppendLine(Truncate(entry.Message.Trim(), MaxDetailLength));
    }

    private static string CollapseWhitespace(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "...";

    private static readonly string[] Severity =
        ["Verbose", "Debug", "Information", "Warning", "Error", "Fatal"];

    private static bool IsAtLeast(string level, string minimum)
    {
        var levelRank = Array.FindIndex(
            Severity,
            value => value.Equals(level, StringComparison.OrdinalIgnoreCase));
        var minimumRank = Array.FindIndex(
            Severity,
            value => value.Equals(minimum, StringComparison.OrdinalIgnoreCase));
        return levelRank >= 0 && minimumRank >= 0 && levelRank >= minimumRank;
    }
}
