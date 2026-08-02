namespace DriverUpdater.App.Ai;

/// <summary>
/// Extracts the machine-readable action lines that the driver chat prompt asks the AI to append.
/// Returns the user-facing prose separately from install, scan, and settings actions so the UI can
/// render native buttons without showing protocol details to the user.
/// </summary>
public static class DriverChatActionParser
{
    private const string RecommendUpdateMarker = "RECOMMEND_UPDATE:";
    private const string ScanNowMarker = "SCAN_NOW";
    private const string SetOptionMarker = "SET_OPTION:";

    public static (string Text, IReadOnlyList<string> HardwareIds, bool RequestsScan,
        IReadOnlyList<ChatSettingChange> SettingChanges) Parse(string answer)
    {
        ArgumentNullException.ThrowIfNull(answer);

        var ids = new List<string>();
        var settingChanges = new List<ChatSettingChange>();
        var kept = new List<string>();
        var requestsScan = false;
        foreach (var line in answer.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r').Trim();
            if (trimmed.StartsWith(RecommendUpdateMarker, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var raw in trimmed[RecommendUpdateMarker.Length..].Split(';', ','))
                {
                    var id = raw.Trim();
                    if (id.Length > 0 && !ids.Contains(id, StringComparer.OrdinalIgnoreCase))
                    {
                        ids.Add(id);
                    }
                }
            }
            else if (trimmed.StartsWith(SetOptionMarker, StringComparison.OrdinalIgnoreCase))
            {
                // The marker line is dropped whether or not it resolves: an invented key must
                // never leak into the prose the user reads.
                AddSettingChanges(trimmed[SetOptionMarker.Length..], settingChanges);
            }
            else if (trimmed.Equals(ScanNowMarker, StringComparison.OrdinalIgnoreCase))
            {
                requestsScan = true;
            }
            else
            {
                kept.Add(line.TrimEnd('\r'));
            }
        }

        return (string.Join('\n', kept).Trim(), ids, requestsScan, settingChanges);
    }

    private static void AddSettingChanges(string payload, List<ChatSettingChange> target)
    {
        foreach (var assignment in payload.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = assignment.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            if (!ChatSettingCatalog.TryResolve(
                    assignment[..separator],
                    assignment[(separator + 1)..],
                    out var change))
            {
                continue;
            }

            // The last assignment for a key wins, so a model that corrects itself mid-reply
            // does not produce two conflicting cards.
            target.RemoveAll(existing => string.Equals(existing.Key, change.Key, StringComparison.OrdinalIgnoreCase));
            target.Add(change);
        }
    }
}
