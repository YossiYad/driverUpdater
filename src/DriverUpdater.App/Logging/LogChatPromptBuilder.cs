using System.Text;

namespace DriverUpdater.App.Logging;

/// <summary>
/// Builds the prompt for a multi-turn conversation about the application logs. The underlying
/// <c>IAiTextCompleter</c> is stateless, so each turn re-sends the log context plus the full
/// conversation history and the newest question as one self-contained prompt.
/// </summary>
public static class LogChatPromptBuilder
{
    public static string Build(
        IReadOnlyList<LogEntry> entries,
        IReadOnlyList<LogChatMessage> history,
        string question)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentException.ThrowIfNullOrWhiteSpace(question);

        var logText = LogSummaryPromptBuilder.FormatEntries(entries);

        var sb = new StringBuilder();
        sb.AppendLine("You are assisting a developer in debugging \"DriverUpdater\", a Windows (WPF, .NET) desktop app");
        sb.AppendLine("that scans and updates device drivers. Below are its runtime logs from the current session,");
        sb.AppendLine("followed by an ongoing conversation about them.");
        sb.AppendLine();
        sb.AppendLine("Answer the developer's latest question concisely and accurately, grounded in the logs.");
        sb.AppendLine("Do not invent details that are not supported by the logs. If the logs do not contain the");
        sb.AppendLine("answer, say so plainly and suggest what to look for. Refer to specific timestamps, drivers,");
        sb.AppendLine("or messages when helpful.");
        sb.AppendLine();
        sb.AppendLine("LOGS:");
        sb.AppendLine(logText);
        sb.AppendLine();
        sb.AppendLine("CONVERSATION:");
        foreach (var message in history)
        {
            sb.Append(message.IsUser ? "Developer: " : "Assistant: ").AppendLine(message.Text);
        }
        sb.Append("Developer: ").AppendLine(question);
        sb.Append("Assistant:");
        return sb.ToString();
    }
}
