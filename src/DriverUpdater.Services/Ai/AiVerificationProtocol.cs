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

    public static string BuildPrompt(
        IReadOnlyList<AiVerificationRequest> requests,
        AppLanguage responseLanguage = AppLanguage.English,
        MachineProfile? machine = null,
        bool webSearchEnabled = false,
        bool unattendedRun = false)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a Windows driver advisor and update verification assistant.");
        sb.AppendLine(responseLanguage == AppLanguage.Hebrew
            ? "Write every user-facing text field in clear, natural Hebrew. Keep the JSON property names and enum values in English so the app can parse them. Keep driver names, versions, hardware IDs, and URLs unchanged."
            : "Write every user-facing text field in clear, natural English. Keep the JSON property names and enum values in English so the app can parse them. Keep driver names, versions, hardware IDs, and URLs unchanged.");
        sb.AppendLine("For each driver below, decide TWO things:");
        sb.AppendLine(webSearchEnabled
            ? "1. isGenuinelyNewer: for normal candidate checks, is the candidate truly a newer/different driver than what is already installed? For findLatestWhenNoCandidate=true, search the web for the latest official driver for this exact device/hardware and set true only when you find evidence of a newer version than installed. Set false if the candidate/latest version equals (or is older/same as) the installed version, or if it is clearly the same driver just published under a later date. A false here means the update should NOT be offered."
            : "1. isGenuinelyNewer: for normal candidate checks, is the candidate truly a newer/different driver than what is already installed? For findLatestWhenNoCandidate=true, judge from the data below and your own knowledge, and set true only when you are confident a newer version than installed exists. Set false if the candidate/latest version equals (or is older/same as) the installed version, or if it is clearly the same driver just published under a later date. A false here means the update should NOT be offered.");
        sb.AppendLine(webSearchEnabled
            ? "2. risk: how likely is installing this driver to cause problems (bugs, instability, regressions, known issues)? Use the web to check for reported problems with this exact version when possible. Values: Safe, Caution, HighRisk, Unknown."
            : "2. risk: how likely is installing this driver to cause problems (bugs, instability, regressions, known issues)? Values: Safe, Caution, HighRisk, Unknown.");
        sb.AppendLine();

        // Without web access the model has no way to look anything up. Saying so keeps it from
        // reporting confident "known issues" it never actually checked.
        if (!webSearchEnabled)
        {
            sb.AppendLine("You have NO web access for this request. Do not claim you searched, looked up, or checked any");
            sb.AppendLine("page, and do not invent release notes, issue reports, URLs, or version numbers. When the data");
            sb.AppendLine("below plus your own knowledge is not enough, answer Unknown and say the evidence is thin.");
            sb.AppendLine();
        }

        AppendMachineSection(sb, machine, webSearchEnabled);
        AppendResearchSection(sb, webSearchEnabled);

        if (unattendedRun)
        {
            sb.AppendLine("This is an UNATTENDED scheduled run: nobody is at the machine, nobody will read a warning, and");
            sb.AppendLine("a bad driver can leave the PC without display, network, or boot until someone comes back to it.");
            sb.AppendLine("Hold every verdict to that bar. Endorse only what you would install on this exact machine with");
            sb.AppendLine("no one watching, and prefer Caution or Unknown over an optimistic Safe.");
            sb.AppendLine();
        }

        sb.AppendLine("Recommendation guidance:");
        sb.AppendLine("- Recommend installing only when the candidate appears genuinely newer, matches the hardware/vendor, comes from a trustworthy source, and there are no significant reports of regressions for this exact version.");
        sb.AppendLine("- Recommend waiting or avoiding when reports mention BSODs, boot/display/network/audio regressions, failed installs, firmware risk, wrong device family, or when the version evidence is weak.");
        sb.AppendLine("- Be stricter for display, storage, firmware, chipset, network, and security drivers because a bad update can break boot, graphics, connectivity, or device trust.");
        sb.AppendLine("- Treat vendor-page/advisory results as less certain than direct Windows Update, Microsoft Catalog package, or a known signed vendor installer.");
        sb.AppendLine("- Do not assume the newest driver is always the best fit. Prefer the newest stable, officially supported version for this exact PC/hardware/Windows generation; if an OEM-customized or older stable branch is safer, say so.");
        sb.AppendLine("- Vendors often publish a specific driver branch or version for a hardware generation, model, or product line. When the vendor names one for this hardware, that is the version to recommend even if a higher-numbered driver exists for other hardware, and moving off it counts against the candidate.");
        sb.AppendLine("- Weigh what owners of this exact hardware report about a version. Repeated first-hand reports of crashes, black screens, timeouts, stutter, failed installs, or rollbacks raise the risk and can make an older stable version the recommendation; a single complaint is not evidence.");
        sb.AppendLine();
        sb.AppendLine("Also fill: summary (one short recommendation sentence for a UI badge, e.g. 'Recommended', 'Use caution', 'Avoid for now', or 'Not enough evidence'), rationale (1-3 sentences explaining version evidence, hardware/source match, and reported issues), latestKnownVersion (the version you believe is actually the latest for this device, or null if unsure), latestKnownDate (release date as yyyy-MM-dd, or null), latestKnownUrl (official vendor/Microsoft support or download page URL, or null).");
        sb.AppendLine("Driver-advisor feedback fields:");
        sb.AppendLine("- installedSuitability: one sentence about whether the currently installed driver appears suitable for this PC/hardware/Windows setup.");
        sb.AppendLine("- candidateSuitability: one sentence about whether the candidate/latest driver appears suitable for this PC/hardware/Windows setup, including OEM-vs-generic concerns when relevant.");
        sb.AppendLine("- recommendedVersion: the version you would actually recommend for this PC, which may be the installed version, the latest version, or another stable/OEM version; null if unsure.");
        sb.AppendLine("- advisorNote: short practical advice for the user, such as install, keep current, use OEM tool, wait, or only update if fixing a specific issue.");
        sb.AppendLine(webSearchEnabled
            ? "- sources: the URLs you actually opened for this driver, official vendor and OEM pages first, then release notes, then user reports. Leave the array empty only when you found nothing usable."
            : "- sources: leave this as an empty array; you had no web access for this request.");
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
        sb.AppendLine("{\"verdicts\":[{\"id\":\"<id>\",\"isGenuinelyNewer\":true,\"risk\":\"Safe\",\"summary\":\"...\",\"rationale\":\"...\",\"latestKnownVersion\":\"...\",\"latestKnownDate\":\"2026-01-31\",\"latestKnownUrl\":\"https://...\",\"installedSuitability\":\"...\",\"candidateSuitability\":\"...\",\"recommendedVersion\":\"...\",\"advisorNote\":\"...\",\"sources\":[\"https://...\"]}]}");
        return sb.ToString();
    }

    // A driver verdict is only meaningful for a specific machine: vendors publish per model,
    // OEM-customized packages beat generic ones on laptops, and a version that is fine on one
    // chipset generation regresses on another. Without this block the model was reasoning about
    // the device class in the abstract.
    private static void AppendMachineSection(StringBuilder sb, MachineProfile? machine, bool webSearchEnabled)
    {
        if (machine is null || !machine.HasAnyDetail)
        {
            return;
        }

        sb.AppendLine("THIS PC (the machine every verdict is for):");
        sb.AppendLine(machine.Describe());
        sb.AppendLine("Judge every driver against this exact machine, not against the device in general. An OEM-supplied");
        sb.AppendLine("package for this model usually beats a generic vendor build on a laptop, and a version that is");
        sb.AppendLine("fine on other hardware can regress on this chipset, GPU, or Windows build.");
        if (webSearchEnabled)
        {
            sb.AppendLine("When you search, put these details in the query: the system model, the motherboard, the CPU, the");
            sb.AppendLine("GPU, and the Windows build, alongside the device name and the version numbers involved.");
        }
        sb.AppendLine();
    }

    // A driver recommendation is only as good as what it was checked against. Without this the
    // model answers from memory and its "latest is best" instinct, which is wrong whenever a
    // vendor pins a hardware generation to a particular driver branch, or whenever a version is
    // fine everywhere except on this hardware.
    private static void AppendResearchSection(StringBuilder sb, bool webSearchEnabled)
    {
        if (!webSearchEnabled)
        {
            return;
        }

        sb.AppendLine("RESEARCH BEFORE YOU ANSWER. Never recommend, endorse, or rate a version from memory alone. Every");
        sb.AppendLine("recommendation must rest on pages you actually opened for this request:");
        sb.AppendLine("1. The vendor's own download and support pages for this exact device, model, or product family,");
        sb.AppendLine("   including any statement that ties a driver branch or version to a hardware generation, product");
        sb.AppendLine("   line, or OEM model. If the vendor names one for this hardware, that is what you recommend, even");
        sb.AppendLine("   when a higher-numbered driver exists for other hardware.");
        sb.AppendLine("2. The release notes and known-issues list of the version you are about to recommend.");
        sb.AppendLine("3. What owners of this exact hardware report about that version: the vendor's own community forum,");
        sb.AppendLine("   Reddit, and comparable discussion sites. Recurring first-hand reports of crashes, black screens,");
        sb.AppendLine("   driver timeouts, stutter, failed installs, or rollbacks raise the risk and can make an older");
        sb.AppendLine("   stable version the right recommendation.");
        sb.AppendLine("Weigh official vendor and OEM statements highest, then release notes, then repeated user reports for");
        sb.AppendLine("this hardware. A single complaint is not evidence that a driver is bad.");
        sb.AppendLine("Keep recommendedVersion consistent with what those pages say, and list the URLs you used in sources.");
        sb.AppendLine("If nothing usable turns up for this exact hardware, say so in the rationale and answer Unknown rather");
        sb.AppendLine("than guessing.");
        sb.AppendLine();
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
                    AdvisorNote: NullIfWhiteSpace(v.AdvisorNote),
                    Sources: NormalizeSources(v.Sources));
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

    private static IReadOnlyList<string>? NormalizeSources(List<string>? raw)
    {
        if (raw is null)
        {
            return null;
        }

        var sources = raw
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Select(url => url.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToArray();
        return sources.Length == 0 ? null : sources;
    }

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
        string? AdvisorNote,
        List<string>? Sources);
}
