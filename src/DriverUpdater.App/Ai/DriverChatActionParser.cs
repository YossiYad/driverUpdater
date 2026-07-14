namespace DriverUpdater.App.Ai;

/// <summary>
/// Extracts the machine-readable RECOMMEND_UPDATE action line that the driver chat prompt asks
/// the AI to append when it recommends installing specific drivers. Returns the prose without
/// the action line plus the recommended hardware IDs, so the UI can offer a one-click install.
/// </summary>
public static class DriverChatActionParser
{
    private const string Marker = "RECOMMEND_UPDATE:";

    public static (string Text, IReadOnlyList<string> HardwareIds) Parse(string answer)
    {
        ArgumentNullException.ThrowIfNull(answer);

        var ids = new List<string>();
        var kept = new List<string>();
        foreach (var line in answer.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r').Trim();
            if (trimmed.StartsWith(Marker, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var raw in trimmed[Marker.Length..].Split(';', ','))
                {
                    var id = raw.Trim();
                    if (id.Length > 0 && !ids.Contains(id, StringComparer.OrdinalIgnoreCase))
                    {
                        ids.Add(id);
                    }
                }
            }
            else
            {
                kept.Add(line.TrimEnd('\r'));
            }
        }

        return (string.Join('\n', kept).Trim(), ids);
    }
}
