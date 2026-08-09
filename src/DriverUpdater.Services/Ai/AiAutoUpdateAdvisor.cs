using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using Microsoft.Extensions.Logging;

namespace DriverUpdater.Services.Ai;

// Reuses the same verification protocol the interactive "Scan with AI" pass uses, then turns
// each verdict into a yes/no install decision for the unattended scheduled run. Everything the
// AI does not clearly endorse is left for the user to install by hand: nobody is watching the
// machine, so "no answer" and "not sure" both mean "do not touch it".
public sealed class AiAutoUpdateAdvisor : IAiAutoUpdateAdvisor
{
    private readonly IAiVerifier _verifier;
    private readonly ILogger<AiAutoUpdateAdvisor> _logger;

    public AiAutoUpdateAdvisor(IAiVerifier verifier, ILogger<AiAutoUpdateAdvisor> logger)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(logger);
        _verifier = verifier;
        _logger = logger;
    }

    public bool IsConfigured => _verifier.IsConfigured;

    public async Task<IReadOnlyList<AiUpdateDecision>> ReviewAsync(
        IReadOnlyList<AiUpdateReviewItem> items,
        AiAutoUpdateRiskTolerance riskTolerance,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
        {
            return Array.Empty<AiUpdateDecision>();
        }

        // One installer can cover many device rows. Review it once and let the caller fan the
        // decision out, mirroring how the install pass dedupes by SourceUpdateId.
        var unique = items
            .GroupBy(i => i.Candidate.SourceUpdateId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToArray();

        var requests = unique.Select(BuildRequest).ToArray();
        _logger.LogInformation(
            "AI auto-update review: provider={Provider}, tolerance={Tolerance}, {Count} unique update(s) from {Rows} row(s)",
            _verifier.Provider, riskTolerance, requests.Length, items.Count);

        var verdicts = await _verifier
            .VerifyAsync(requests, unattendedRun: true, cancellationToken)
            .ConfigureAwait(false);

        var decisions = new List<AiUpdateDecision>(unique.Length);
        foreach (var item in unique)
        {
            var id = item.Candidate.SourceUpdateId;
            if (!verdicts.TryGetValue(id, out var verdict))
            {
                decisions.Add(new AiUpdateDecision(
                    id,
                    item.Driver.DeviceName,
                    ShouldInstall: false,
                    Reason: "the AI returned no verdict for this update"));
                continue;
            }

            decisions.Add(Decide(item, verdict, riskTolerance));
        }

        return decisions;
    }

    private static AiUpdateDecision Decide(
        AiUpdateReviewItem item,
        AiVerdict verdict,
        AiAutoUpdateRiskTolerance riskTolerance)
    {
        var id = item.Candidate.SourceUpdateId;
        var device = item.Driver.DeviceName;

        if (!verdict.IsGenuinelyNewer)
        {
            return new AiUpdateDecision(
                id,
                device,
                ShouldInstall: false,
                Reason: Describe("the AI does not consider this a genuine upgrade", verdict),
                Verdict: verdict);
        }

        if (!IsWithinTolerance(verdict.Risk, riskTolerance))
        {
            return new AiUpdateDecision(
                id,
                device,
                ShouldInstall: false,
                Reason: Describe($"risk rated {verdict.Risk}, above the configured tolerance {riskTolerance}", verdict),
                Verdict: verdict);
        }

        return new AiUpdateDecision(
            id,
            device,
            ShouldInstall: true,
            Reason: Describe($"risk rated {verdict.Risk}", verdict),
            Verdict: verdict);
    }

    private static bool IsWithinTolerance(AiRiskLevel risk, AiAutoUpdateRiskTolerance tolerance) => risk switch
    {
        AiRiskLevel.Safe => true,
        AiRiskLevel.Caution => tolerance == AiAutoUpdateRiskTolerance.SafeAndCaution,
        _ => false
    };

    private static string Describe(string reason, AiVerdict verdict)
    {
        var summary = verdict.Summary?.Trim();
        return string.IsNullOrEmpty(summary) ? reason : $"{reason}: {summary}";
    }

    private static AiVerificationRequest BuildRequest(AiUpdateReviewItem item) =>
        new(
            CorrelationId: item.Candidate.SourceUpdateId,
            DeviceName: item.Driver.DeviceName,
            HardwareId: item.Driver.HardwareId,
            InstalledVersion: item.Driver.CurrentVersion?.ToString(),
            InstalledDate: item.Driver.CurrentDate,
            CandidateVersion: item.Candidate.DisplayVersion,
            CandidateDate: item.Candidate.NewDate,
            Source: item.Candidate.Source,
            DownloadUrl: item.Candidate.DownloadUrl.AbsoluteUri,
            Category: item.Driver.Category,
            Provider: item.Driver.Provider,
            Manufacturer: item.Driver.Manufacturer,
            InstallKind: item.Candidate.InstallKind,
            Confidence: item.Candidate.Confidence);
}
