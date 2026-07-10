using System.Text;

namespace DriverUpdater.App.Logging;

/// <summary>
/// Builds the prompt sent to the AI to turn the raw application logs into a concise,
/// developer-facing diagnostic summary that the user can copy and share.
/// </summary>
public static class LogSummaryPromptBuilder
{
    // Keep the log slice well within model context limits. The most recent entries are the
    // most relevant, so when the buffer is large we keep the tail.
    private const int MaxLogChars = 24000;

    public static string Build(IReadOnlyList<LogEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var errors = entries.Count(e => IsAtLeast(e.Level, "Error"));
        var warnings = entries.Count(e => string.Equals(e.Level, "Warning", StringComparison.OrdinalIgnoreCase));
        var logText = FormatEntries(entries);

        var sb = new StringBuilder();
        sb.AppendLine("You are assisting a developer in debugging \"DriverUpdater\", a Windows (WPF, .NET) desktop app");
        sb.AppendLine("that scans and updates device drivers. Below are its runtime logs from the current session.");
        sb.AppendLine();
        sb.AppendLine("Write a concise diagnostic summary in English that the developer can paste into a bug report.");
        sb.AppendLine("Structure it as short bullet points covering:");
        sb.AppendLine("- What the app was doing (main activities in this session).");
        sb.AppendLine("- Every error, warning, or failure, with its likely root cause.");
        sb.AppendLine("- Repeated or anomalous patterns worth attention.");
        sb.AppendLine("- Concrete suggested next steps to fix problems or improve the app.");
        sb.AppendLine("Do not invent details that are not supported by the logs. Keep it under ~400 words.");
        sb.AppendLine();
        sb.AppendLine($"Session log stats: {entries.Count} entries, {errors} error(s)/fatal, {warnings} warning(s).");
        sb.AppendLine();
        sb.AppendLine("LOGS:");
        sb.AppendLine(logText);
        return sb.ToString();
    }

    private static string FormatEntries(IReadOnlyList<LogEntry> entries)
    {
        var buffer = new StringBuilder();
        foreach (var entry in entries)
        {
            buffer.Append(entry.Timestamp.ToString("HH:mm:ss.fff"));
            buffer.Append(" [").Append(entry.Level).Append(']');
            if (!string.IsNullOrEmpty(entry.Category))
            {
                buffer.Append(' ').Append(entry.Category).Append(':');
            }
            buffer.Append(' ').Append(entry.Message).AppendLine();
            if (!string.IsNullOrEmpty(entry.Exception))
            {
                buffer.AppendLine(entry.Exception);
            }
        }

        var text = buffer.ToString();
        if (text.Length <= MaxLogChars)
        {
            return text;
        }

        var tail = text[^MaxLogChars..];
        return $"... (older entries omitted, showing the last {MaxLogChars} characters)\n{tail}";
    }

    private static readonly string[] Severity = { "Verbose", "Debug", "Information", "Warning", "Error", "Fatal" };

    private static bool IsAtLeast(string level, string minimum)
    {
        var levelRank = Array.FindIndex(Severity, s => s.Equals(level, StringComparison.OrdinalIgnoreCase));
        var minRank = Array.FindIndex(Severity, s => s.Equals(minimum, StringComparison.OrdinalIgnoreCase));
        return levelRank >= 0 && minRank >= 0 && levelRank >= minRank;
    }
}
