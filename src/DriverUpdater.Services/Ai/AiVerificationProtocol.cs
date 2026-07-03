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
        sb.AppendLine("You are a Windows driver advisor and update verification assistant.");
        sb.AppendLine("For each driver below, decide TWO things:");
        sb.AppendLine("1. isGenuinelyNewer: for normal candidate checks, is the candidate truly a newer/different driver than what is already installed? For findLatestWhenNoCandidate=true, search the web for the latest official driver for this exact device/hardware and set true only when you find evidence of a newer version than installed. Set false if the candidate/latest version equals (or is older/same as) the installed version, or if it is clearly the same driver just published under a later date. A false here means the update should NOT be offered.");
        sb.AppendLine("2. risk: how likely is installing this driver to cause problems (bugs, instability, regressions, known issues)? Use the web to check for reported problems with this exact version when possible. Values: Safe, Caution, HighRisk, Unknown.");
        sb.AppendLine();
        sb.AppendLine("Recommendation guidance:");
        sb.AppendLine("- Recommend installing only when the candidate appears genuinely newer, matches the hardware/vendor, comes from a trustworthy source, and there are no significant reports of regressions for this exact version.");
        sb.AppendLine("- Recommend waiting or avoiding when reports mention BSODs, boot/display/network/audio regressions, failed installs, firmware risk, wrong device family, or when the version evidence is weak.");
        sb.AppendLine("- Be stricter for display, storage, firmware, chipset, network, and security drivers because a bad update can break boot, graphics, connectivity, or device trust.");
        sb.AppendLine("- Treat vendor-page/advisory results as less certain than direct Windows Update, Microsoft Catalog package, or a known signed vendor installer.");
        sb.AppendLine("- Do not assume the newest driver is always the best fit. Prefer the newest stable, officially supported version for this exact PC/hardware/Windows generation; if an OEM-customized or older stable branch is safer, say so.");
        sb.AppendLine();
        sb.AppendLine("Also fill: summary (one short recommendation sentence for a UI badge, e.g. 'Recommended', 'Use caution', 'Avoid for now', or 'Not enough evidence'), rationale (1-3 sentences explaining version evidence, hardware/source match, and reported issues), latestKnownVersion (the version you believe is actually the latest for this device, or null if unsure), latestKnownDate (release date as yyyy-MM-dd, or null), latestKnownUrl (official vendor/Microsoft support or download page URL, or null).");
        sb.AppendLine("Driver-advisor feedback fields:");
        sb.AppendLine("- installedSuitability: one sentence about whether the currently installed driver appears suitable for this PC/hardware/Windows setup.");
        sb.AppendLine("- candidateSuitability: one sentence about whether the candidate/latest driver appears suitable for this PC/hardware/Windows setup, including OEM-vs-generic concerns when relevant.");
        sb.AppendLine("- recommendedVersion: the version you would actually recommend for this PC, which may be the installed version, the latest version, or another stable/OEM version; null if unsure.");
        sb.AppendLine("- advisorNote: short practical advice for the user, such as install, keep current, use OEM tool, wait, or only update if fixing a specific issue.");
        sb.AppendLine("When findLatestWhenNoCandidate=true, there may be no candidate package yet. In that case, use official vendor, OEM support, Microsoft Catalog, or Microsoft Download Center sources whenever possible and provide latestKnownUrl so the app can offer a vendor-check action. Do not use documentation, learn.microsoft.com articles, issue trackers, search-result pages, or general background pages as latestKnownUrl unless no install/support page exists; if only documentation exists, set isGenuinelyNewer=false or explain that this is advisory-only.");
        sb.AppendLine();
        sb.AppendLine("Candidates:");
        foreach (var r in requests)
        {
            sb.Append("- id=").Append(r.CorrelationId)
                .Append(" | device=").Append(r.DeviceName)
                .Append(" | hardwareId=").Append(r.HardwareId)
                .Append(" | category=").Append(r.Category)
                .Append(" | provider=").Append(r.Provider)
                .Append(" | manufacturer=").Append(r.Manufacturer)
                .Append(" | installedVersion=").Append(r.InstalledVersion ?? "unknown")
                .Append(" | installedDate=").Append(r.InstalledDate?.ToString("yyyy-MM-dd") ?? "unknown")
                .Append(" | candidateVersion=").Append(r.CandidateVersion)
                .Append(" | candidateDate=").Append(r.CandidateDate.ToString("yyyy-MM-dd"))
                .Append(" | source=").Append(r.Source)
                .Append(" | installKind=").Append(r.InstallKind)
                .Append(" | confidence=").Append(r.Confidence)
                .Append(" | findLatestWhenNoCandidate=").Append(r.FindLatestWhenNoCandidate)
                .Append(" | url=").Append(r.DownloadUrl)
                .AppendLine();
        }
        sb.AppendLine();
        sb.AppendLine("Respond with ONLY a JSON object, no markdown, in exactly this shape:");
        sb.AppendLine("{\"verdicts\":[{\"id\":\"<id>\",\"isGenuinelyNewer\":true,\"risk\":\"Safe\",\"summary\":\"...\",\"rationale\":\"...\",\"latestKnownVersion\":\"...\",\"latestKnownDate\":\"2026-01-31\",\"latestKnownUrl\":\"https://...\",\"installedSuitability\":\"...\",\"candidateSuitability\":\"...\",\"recommendedVersion\":\"...\",\"advisorNote\":\"...\"}]}");
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
                    LatestKnownVersion: string.IsNullOrWhiteSpace(v.LatestKnownVersion) ? null : v.LatestKnownVersion,
                    LatestKnownDate: ParseDate(v.LatestKnownDate),
                    LatestKnownUrl: string.IsNullOrWhiteSpace(v.LatestKnownUrl) ? null : v.LatestKnownUrl,
                    InstalledSuitability: NullIfWhiteSpace(v.InstalledSuitability),
                    CandidateSuitability: NullIfWhiteSpace(v.CandidateSuitability),
                    RecommendedVersion: NullIfWhiteSpace(v.RecommendedVersion),
                    AdvisorNote: NullIfWhiteSpace(v.AdvisorNote));
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
            if (depth == 0 && c != '{')
            {
                continue;
            }

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

    private static DateOnly? ParseDate(string? raw) =>
        DateOnly.TryParse(raw, out var date) ? date : null;

    private static string? NullIfWhiteSpace(string? raw) =>
        string.IsNullOrWhiteSpace(raw) ? null : raw;

    private sealed record VerdictsEnvelope(List<VerdictDto>? Verdicts);

    private sealed record VerdictDto(
        string? Id,
        bool IsGenuinelyNewer,
        string? Risk,
        string? Summary,
        string? Rationale,
        string? LatestKnownVersion,
        string? LatestKnownDate,
        string? LatestKnownUrl,
        string? InstalledSuitability,
        string? CandidateSuitability,
        string? RecommendedVersion,
        string? AdvisorNote);
}
