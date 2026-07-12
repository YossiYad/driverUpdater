using System.Text;
using DriverUpdater.App.Logging;

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

    public static string Build(
        IReadOnlyList<DriverChatContextItem> drivers,
        IReadOnlyList<LogChatMessage> history,
        string question)
    {
        ArgumentNullException.ThrowIfNull(drivers);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentException.ThrowIfNullOrWhiteSpace(question);

        var sb = new StringBuilder();
        sb.AppendLine("You are a Windows driver advisor inside \"DriverUpdater\", a desktop app that scans and updates");
        sb.AppendLine("device drivers. Below is the current scan of this PC's drivers, followed by a conversation.");
        sb.AppendLine();
        sb.AppendLine("Answer the user's latest question concisely and practically, grounded in the driver list.");
        sb.AppendLine("Help them decide what is worth updating and what to leave alone. Be cautious with display,");
        sb.AppendLine("storage, firmware, chipset, network, and security drivers, where a bad update can break boot,");
        sb.AppendLine("graphics, connectivity, or device trust. Prefer the newest STABLE, officially supported driver");
        sb.AppendLine("for this exact hardware; note when an OEM/vendor driver is safer than a generic one. If a driver");
        sb.AppendLine("is not in the list, say you don't see it rather than inventing details.");
        sb.AppendLine();

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

        sb.AppendLine("CONVERSATION:");
        foreach (var message in history)
        {
            sb.Append(message.IsUser ? "User: " : "Assistant: ").AppendLine(message.Text);
        }
        sb.Append("User: ").AppendLine(question);
        sb.Append("Assistant:");
        return sb.ToString();
    }

    private static string Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "(unknown)" : value.Replace('\n', ' ').Replace('\r', ' ').Trim();
}
