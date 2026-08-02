namespace DriverUpdater.Core.Models;

/// <summary>
/// How much risk an <see cref="AutoUpdateScope.AiRecommended"/> run may accept. The AI rates
/// every candidate with an <see cref="AiRiskLevel"/>; anything above the chosen tolerance,
/// and anything the AI could not rate, is left for a manual install.
/// </summary>
public enum AiAutoUpdateRiskTolerance
{
    SafeOnly = 0,
    SafeAndCaution
}
