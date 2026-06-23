using System.Text;
using System.Text.Json;
using DriverUpdater.Core.Models;

namespace DriverUpdater.Services.Ai;

// Shared prompt construction and response parsing for every AI provider. Both Gemini and
// Ollama get the same prompt and return the same JSON shape; only the transport differs.
internal static class AiVerificationProtocol
{
    private static readonly JsonSerializerOptions ParseOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static string BuildPrompt(IReadOnlyList<AiVerificationRequest> requests)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a Windows driver-update verification assistant.");
        sb.AppendLine("For each candidate driver update below, decide TWO things:");
        sb.AppendLine("1. isGenuinelyNewer: is the candidate truly a newer/different driver than what is already installed? Set false if the candidate version equals (or is older/same as) the installed version, or if it is clearly the same driver just published under a later date. A false here means the update should NOT be offered.");
        sb.AppendLine("2. risk: how likely is installing this driver to cause problems (bugs, instability, regressions, known issues)? Use the web to check for reported problems with this exact version when possible. Values: Safe, Caution, HighRisk, Unknown.");
        sb.AppendLine();
        sb.AppendLine("Also fill: summary (one short sentence for a UI badge), rationale (1-3 sentences explaining your reasoning), latestKnownVersion (the version you believe is actually the latest for this device, or null if unsure).");
        sb.AppendLine();
        sb.AppendLine("Candidates:");
        foreach (var r in requests)
        {
            sb.Append("- id=").Append(r.CorrelationId)
                .Append(" | device=").Append(r.DeviceName)
                .Append(" | hardwareId=").Append(r.HardwareId)
                .Append(" | installedVersion=").Append(r.InstalledVersion ?? "unknown")
                .Append(" | installedDate=").Append(r.InstalledDate?.ToString("yyyy-MM-dd") ?? "unknown")
                .Append(" | candidateVersion=").Append(r.CandidateVersion)
                .Append(" | candidateDate=").Append(r.CandidateDate.ToString("yyyy-MM-dd"))
                .Append(" | source=").Append(r.Source)
                .Append(" | url=").Append(r.DownloadUrl)
                .AppendLine();
        }
        sb.AppendLine();
        sb.AppendLine("Respond with ONLY a JSON object, no markdown, in exactly this shape:");
        sb.AppendLine("{\"verdicts\":[{\"id\":\"<id>\",\"isGenuinelyNewer\":true,\"risk\":\"Safe\",\"summary\":\"...\",\"rationale\":\"...\",\"latestKnownVersion\":\"...\"}]}");
        return sb.ToString();
    }

    // Tolerant parse: providers with web grounding can wrap JSON in prose or markdown
    // fences, so scan for balanced JSON objects and parse the first valid verdict payload.
    public static IReadOnlyDictionary<string, AiVerdict> ParseVerdicts(string? rawText)
    {
        var result = new Dictionary<string, AiVerdict>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return result;
        }

        VerdictsEnvelope? envelope;
        foreach (var json in ExtractJsonObjects(rawText))
        {
            try
            {
                envelope = JsonSerializer.Deserialize<VerdictsEnvelope>(json, ParseOptions);
            }
            catch (JsonException)
            {
                continue;
            }

            if (envelope?.Verdicts is null)
            {
                continue;
            }

            foreach (var v in envelope.Verdicts)
            {
                if (string.IsNullOrWhiteSpace(v.Id))
                {
                    continue;
                }
                result[v.Id] = new AiVerdict(
                    IsGenuinelyNewer: v.IsGenuinelyNewer,
                    Risk: ParseRisk(v.Risk),
                    Summary: v.Summary ?? string.Empty,
                    Rationale: v.Rationale ?? string.Empty,
                    LatestKnownVersion: string.IsNullOrWhiteSpace(v.LatestKnownVersion) ? null : v.LatestKnownVersion);
            }

            if (result.Count > 0)
            {
                return result;
            }
        }

        return result;
    }

    private static IEnumerable<string> ExtractJsonObjects(string text)
    {
        var start = -1;
        var depth = 0;
        var inString = false;
        var isEscaped = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (isEscaped)
            {
                isEscaped = false;
                continue;
            }

            if (inString && c == '\\')
            {
                isEscaped = true;
                continue;
            }

            if (c == '"')
            {
                inString = !inString;
                continue;
            }

            if (inString)
            {
                continue;
            }

            if (c == '{')
            {
                if (depth == 0)
                {
                    start = i;
                }
                depth++;
                continue;
            }

            if (c != '}' || depth == 0)
            {
                continue;
            }

            depth--;
            if (depth == 0 && start >= 0)
            {
                yield return text.Substring(start, i - start + 1);
                start = -1;
            }
        }
    }

    private static AiRiskLevel ParseRisk(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "safe" => AiRiskLevel.Safe,
        "caution" => AiRiskLevel.Caution,
        "highrisk" or "high risk" or "high" => AiRiskLevel.HighRisk,
        _ => AiRiskLevel.Unknown
    };

    private sealed record VerdictsEnvelope(List<VerdictDto>? Verdicts);

    private sealed record VerdictDto(
        string? Id,
        bool IsGenuinelyNewer,
        string? Risk,
        string? Summary,
        string? Rationale,
        string? LatestKnownVersion);
}
