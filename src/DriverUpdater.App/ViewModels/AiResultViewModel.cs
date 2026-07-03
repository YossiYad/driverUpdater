using DriverUpdater.Core.Models;

namespace DriverUpdater.App.ViewModels;

public sealed class AiResultViewModel
{
    public AiResultViewModel(DriverInfo driver, UpdateCandidate? candidate, AiVerdict verdict)
    {
        ArgumentNullException.ThrowIfNull(driver);
        ArgumentNullException.ThrowIfNull(verdict);

        DeviceName = driver.DeviceName;
        HardwareId = driver.HardwareId;
        Provider = string.IsNullOrWhiteSpace(driver.Provider) ? "Unknown" : driver.Provider;
        Manufacturer = string.IsNullOrWhiteSpace(driver.Manufacturer) ? "Unknown" : driver.Manufacturer;
        Category = driver.Category.ToString();
        InstalledVersion = driver.CurrentVersion?.ToString() ?? "Unknown";
        InstalledDate = driver.CurrentDate?.ToString("yyyy-MM-dd") ?? "Unknown";
        CandidateVersion = candidate?.NewVersion.ToString() ?? verdict.LatestKnownVersion ?? "No candidate";
        CandidateDate = candidate?.NewDate.ToString("yyyy-MM-dd")
            ?? verdict.LatestKnownDate?.ToString("yyyy-MM-dd")
            ?? "Unknown";
        Source = candidate?.Source.ToString() ?? "AI latest-driver search";
        InstallKind = candidate?.InstallKind.ToString() ?? "Advisory";
        Risk = verdict.Risk.ToString();
        Summary = EmptyFallback(verdict.Summary);
        Rationale = EmptyFallback(verdict.Rationale);
        LatestKnownVersion = verdict.LatestKnownVersion ?? "Unknown";
        LatestKnownDate = verdict.LatestKnownDate?.ToString("yyyy-MM-dd") ?? "Unknown";
        LatestKnownUrl = verdict.LatestKnownUrl ?? candidate?.DownloadUrl.AbsoluteUri ?? "Unknown";
        InstalledSuitability = EmptyFallback(verdict.InstalledSuitability);
        CandidateSuitability = EmptyFallback(verdict.CandidateSuitability);
        RecommendedVersion = verdict.RecommendedVersion ?? "Unknown";
        AdvisorNote = EmptyFallback(verdict.AdvisorNote);
        Recommendation = BuildRecommendation(verdict);
    }

    public string DeviceName { get; }
    public string HardwareId { get; }
    public string Provider { get; }
    public string Manufacturer { get; }
    public string Category { get; }
    public string InstalledVersion { get; }
    public string InstalledDate { get; }
    public string CandidateVersion { get; }
    public string CandidateDate { get; }
    public string Source { get; }
    public string InstallKind { get; }
    public string Risk { get; }
    public string Summary { get; }
    public string Rationale { get; }
    public string LatestKnownVersion { get; }
    public string LatestKnownDate { get; }
    public string LatestKnownUrl { get; }
    public string InstalledSuitability { get; }
    public string CandidateSuitability { get; }
    public string RecommendedVersion { get; }
    public string AdvisorNote { get; }
    public string Recommendation { get; }

    public string CopyText => string.Join(
        Environment.NewLine,
        new[]
        {
            $"Device: {DeviceName}",
            $"Hardware ID: {HardwareId}",
            $"Provider: {Provider}",
            $"Manufacturer: {Manufacturer}",
            $"Category: {Category}",
            string.Empty,
            "AI recommendation",
            $"Recommendation: {Recommendation}",
            $"Risk: {Risk}",
            $"Summary: {Summary}",
            $"Rationale: {Rationale}",
            string.Empty,
            "Installed driver",
            $"Version: {InstalledVersion}",
            $"Date: {InstalledDate}",
            $"Suitability: {InstalledSuitability}",
            string.Empty,
            "Candidate / latest",
            $"Version: {CandidateVersion}",
            $"Date: {CandidateDate}",
            $"Source: {Source}",
            $"Install kind: {InstallKind}",
            $"Suitability: {CandidateSuitability}",
            string.Empty,
            "Recommended version",
            RecommendedVersion,
            string.Empty,
            "AI advice",
            AdvisorNote,
            string.Empty,
            "Latest known",
            $"Version: {LatestKnownVersion}",
            $"Date: {LatestKnownDate}",
            $"URL: {LatestKnownUrl}"
        });

    private static string EmptyFallback(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "No detail provided." : value;

    private static string BuildRecommendation(AiVerdict verdict)
    {
        if (!verdict.IsGenuinelyNewer)
        {
            return "Do not install";
        }

        return verdict.Risk switch
        {
            AiRiskLevel.Safe => "Recommended",
            AiRiskLevel.Caution => "Use caution",
            AiRiskLevel.HighRisk => "Avoid for now",
            _ => "Not enough evidence"
        };
    }
}
