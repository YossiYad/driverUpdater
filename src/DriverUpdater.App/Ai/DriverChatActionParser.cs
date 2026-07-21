namespace DriverUpdater.App.Ai;

/// <summary>
/// Extracts the machine-readable action lines that the driver chat prompt asks the AI to append.
/// Returns the user-facing prose separately from install and scan actions so the UI can render
/// native buttons without showing protocol details to the user.
/// </summary>
public static class DriverChatActionParser
{
    private const string RecommendUpdateMarker = "RECOMMEND_UPDATE:";
    private const string ScanNowMarker = "SCAN_NOW";

    public static (string Text, IReadOnlyList<string> HardwareIds, bool RequestsScan) Parse(string answer)
    {
        ArgumentNullException.ThrowIfNull(answer);

        var ids = new List<string>();
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
            else if (trimmed.Equals(ScanNowMarker, StringComparison.OrdinalIgnoreCase))
            {
                requestsScan = true;
            }
            else
            {
                kept.Add(line.TrimEnd('\r'));
            }
        }

        return (string.Join('\n', kept).Trim(), ids, requestsScan);
    }
}
