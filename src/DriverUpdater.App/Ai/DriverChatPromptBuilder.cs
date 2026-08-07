using System.Text;
using DriverUpdater.App.Logging;
using DriverUpdater.Core.Models;
using DriverUpdater.Core.Options;

namespace DriverUpdater.App.Ai;

/// <summary>One driver row's essentials, formatted into the driver-chat prompt.</summary>
public sealed record DriverChatContextItem(
    string DeviceName,
    string HardwareId,
    string Category,
    string? CurrentVersion,
    string Status,
    string? AvailableVersion,
    string? AvailableSource);

/// <summary>
/// Builds the prompt for a multi-turn conversation about the scanned drivers. The underlying
/// <c>IAiTextCompleter</c> is stateless, so each turn re-sends the driver inventory plus the full
/// conversation history and the newest question as one self-contained prompt.
/// </summary>
public static class DriverChatPromptBuilder
{
    // Keep the prompt bounded on machines with hundreds of devices.
    private const int MaxDrivers = 400;

    // The driver inventory already fills most of the prompt, so the log slice gets a smaller
    // budget than the dedicated logs-window chat. The tail is kept, which is where an ongoing
    // update run's progress lines live.
    private const int MaxLogChars = 12000;

    public static string Build(
        IReadOnlyList<DriverChatContextItem> drivers,
        IReadOnlyList<LogChatMessage> history,
        string question,
        AppLanguage responseLanguage = AppLanguage.English,
        bool allowInstallActions = true,
        AppSettings? settings = null,
        IReadOnlyList<LogEntry>? recentLogs = null,
        bool updateRunInProgress = false)
    {
        ArgumentNullException.ThrowIfNull(drivers);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentException.ThrowIfNullOrWhiteSpace(question);

        var sb = new StringBuilder();
        sb.AppendLine("You are a Windows driver advisor inside \"DriverUpdater\", a desktop app that scans and updates");
        sb.AppendLine("device drivers. Below is the current scan of this PC's drivers, followed by a conversation.");
        sb.AppendLine();
        sb.AppendLine(responseLanguage == AppLanguage.Hebrew
            ? "Write every user-facing answer in clear, natural Hebrew. Keep driver names, model names, versions, hardware IDs, and URLs unchanged."
            : "Write every user-facing answer in clear, natural English. Keep driver names, model names, versions, hardware IDs, and URLs unchanged.");
        sb.AppendLine("Answer the user's latest question concisely and practically, grounded in the driver list.");
        sb.AppendLine("Help them decide what is worth updating and what to leave alone. Be cautious with display,");
        sb.AppendLine("storage, firmware, chipset, network, and security drivers, where a bad update can break boot,");
        sb.AppendLine("graphics, connectivity, or device trust. Prefer the newest STABLE, officially supported driver");
        sb.AppendLine("for this exact hardware; note when an OEM/vendor driver is safer than a generic one. If a driver");
        sb.AppendLine("is not in the list, say you don't see it rather than inventing details.");
        sb.AppendLine();
        if (allowInstallActions)
        {
            sb.AppendLine("When you conclude that specific drivers from the list should be updated now - either because the");
            sb.AppendLine("user asked you to update/install them or because they asked what to install and you recommend");
            sb.AppendLine("specific ones - finish your reply with one extra line, exactly in this format:");
            sb.AppendLine("RECOMMEND_UPDATE: <hardwareId>; <hardwareId>");
            sb.AppendLine("Rules for that line: use only hardware IDs copied exactly from the driver list below, only for");
            sb.AppendLine("drivers that show an available update, and put it on its own line at the very end. Do not talk");
            sb.AppendLine("about the line itself in your prose; the app turns it into an install button the user can press.");
            sb.AppendLine("If nothing should be installed, do not output that line at all.");
            sb.AppendLine("If the user asks what you recommend updating and the driver list shows zero available updates,");
            sb.AppendLine("clearly say that you do not currently see available updates in this scan, then finish with one");
            sb.AppendLine("extra line containing exactly SCAN_NOW. Put it on its own line at the very end. The app turns it");
            sb.AppendLine("into a Scan now button. Never output both SCAN_NOW and RECOMMEND_UPDATE in the same reply.");
        }
        else
        {
            sb.AppendLine("The user is asking why you made an earlier recommendation. Explain the reasoning for each named");
            sb.AppendLine("driver, including the installed and available versions, source reliability, expected benefit,");
            sb.AppendLine("hardware or OEM compatibility, meaningful risk, and any uncertainty in the available evidence.");
            sb.AppendLine("Do not recommend additional updates and do not output a RECOMMEND_UPDATE or SCAN_NOW line in this reply.");
        }
        sb.AppendLine();

        if (settings is not null && allowInstallActions)
        {
            AppendSettingsProtocol(sb, settings);
        }

        var withUpdates = drivers.Count(d => !string.IsNullOrEmpty(d.AvailableVersion));
        sb.Append("DRIVERS (").Append(drivers.Count).Append(" total, ").Append(withUpdates).AppendLine(" with an available update):");
        sb.AppendLine("Format: name | hardwareId | category | installed | status | available (source)");
        foreach (var d in drivers.Take(MaxDrivers))
        {
            sb.Append("- ").Append(Clean(d.DeviceName))
                .Append(" | ").Append(Clean(d.HardwareId))
                .Append(" | ").Append(Clean(d.Category))
                .Append(" | ").Append(d.CurrentVersion ?? "?")
                .Append(" | ").Append(Clean(d.Status));
            if (!string.IsNullOrEmpty(d.AvailableVersion))
            {
                sb.Append(" | ").Append(d.AvailableVersion)
                    .Append(" (").Append(d.AvailableSource ?? "unknown").Append(')');
            }
            sb.AppendLine();
        }
        if (drivers.Count > MaxDrivers)
        {
            sb.Append("... and ").Append(drivers.Count - MaxDrivers).AppendLine(" more not shown.");
        }
        sb.AppendLine();

        if (recentLogs is { Count: > 0 })
        {
            AppendRecentLogs(sb, recentLogs, updateRunInProgress);
        }

        sb.AppendLine("CONVERSATION:");
        foreach (var message in history)
        {
            sb.Append(message.IsUser ? "User: " : "Assistant: ").AppendLine(message.Text);
        }
        sb.Append("User: ").AppendLine(question);
        sb.Append("Assistant:");
        return sb.ToString();
    }

    private static void AppendSettingsProtocol(StringBuilder sb, AppSettings settings)
    {
        sb.AppendLine("APP SETTINGS you can change for the user. Current values:");
        foreach (var line in ChatSettingCatalog.DescribeCurrent(settings))
        {
            sb.Append("- ").AppendLine(line);
        }
        sb.AppendLine();
        sb.AppendLine("Keys and the values each one accepts:");
        foreach (var line in ChatSettingCatalog.DescribeOptions())
        {
            sb.Append("- ").AppendLine(line);
        }
        sb.AppendLine();
        sb.AppendLine("Use these to answer questions about how the app is configured. When the user asks you to");
        sb.AppendLine("change something, or when a setting would clearly solve what they are describing, propose it");
        sb.AppendLine("by finishing your reply with one extra line, exactly in this format:");
        sb.AppendLine("SET_OPTION: <key>=<value>; <key>=<value>");
        sb.AppendLine("Rules for that line: use only the keys and values listed above, put it on its own line at the");
        sb.AppendLine("very end, and never mention the line itself in your prose. The app turns it into a card that");
        sb.AppendLine("asks the user to confirm before anything is written, so describe the change in plain language");
        sb.AppendLine("in your prose first. Do not claim the change is already done. Propose settings only when they");
        sb.AppendLine("are relevant; a normal driver question needs no SET_OPTION line. You may offer a helpful");
        sb.AppendLine("setting the user did not ask for, as long as you explain why in one short sentence.");
        sb.AppendLine();
    }

    private static void AppendRecentLogs(StringBuilder sb, IReadOnlyList<LogEntry> recentLogs, bool updateRunInProgress)
    {
        sb.AppendLine("RECENT APPLICATION LOGS (this session, oldest first, each line prefixed with a HH:mm:ss time).");
        sb.AppendLine("They show what the app has been doing, including scan progress and driver update/install runs.");
        sb.AppendLine("Lines starting with \"Update run:\" trace each driver install from start to finish - which");
        sb.AppendLine("driver is being installed, from which source, and whether it succeeded, failed, was skipped,");
        sb.AppendLine("or needs a restart. Use these logs to answer questions about what is happening right now or");
        sb.AppendLine("what happened during an update - the progress so far, errors and their likely cause, and what");
        sb.AppendLine("remains. For questions about speed or slowness (e.g. why an update is taking long), compare");
        sb.AppendLine("the timestamps: identify which step is consuming the time (download, vendor installer,");
        sb.AppendLine("verification, a specific driver) and note that large vendor installers can legitimately take");
        sb.AppendLine("many minutes. If the logs do not contain the answer, say so plainly instead of guessing.");
        if (updateRunInProgress)
        {
            sb.AppendLine("An update/install run is IN PROGRESS right now; the last log lines reflect its live state.");
        }
        sb.AppendLine("LOGS:");
        sb.AppendLine(LogSummaryPromptBuilder.FormatEntries(recentLogs, MaxLogChars));
        sb.AppendLine();
    }

    private static string Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "(unknown)" : value.Replace('\n', ' ').Replace('\r', ' ').Trim();
}
