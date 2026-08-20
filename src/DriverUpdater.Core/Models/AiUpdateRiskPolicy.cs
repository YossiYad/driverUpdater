namespace DriverUpdater.Core.Models;

/// <summary>
/// The single place that decides whether an AI risk rating is low enough to install without
/// the user weighing it up. Shared by the unattended scheduled run and the one-click
/// "Update with AI" button so both hand back the same answer for the same verdict.
/// </summary>
public static class AiUpdateRiskPolicy
{
    public static bool IsWithinTolerance(AiRiskLevel risk, AiAutoUpdateRiskTolerance tolerance) => risk switch
    {
        AiRiskLevel.Safe => true,
        AiRiskLevel.Caution => tolerance == AiAutoUpdateRiskTolerance.SafeAndCaution,
        _ => false
    };

    public static string Describe(AiAutoUpdateRiskTolerance tolerance) => tolerance switch
    {
        AiAutoUpdateRiskTolerance.SafeAndCaution => "safe and caution",
        _ => "safe only"
    };
}
