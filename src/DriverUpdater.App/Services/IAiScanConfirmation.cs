namespace DriverUpdater.App.Services;

/// <summary>What the planned provider requests are for, so the warning can name it.</summary>
public enum AiUsagePurpose
{
    Scan = 0,
    UpdateResearch
}

public sealed record AiScanUsageEstimate(
    int DriverCount,
    int PlannedRequests,
    string Model,
    AiUsagePurpose Purpose = AiUsagePurpose.Scan);

public interface IAiScanConfirmation
{
    Task<bool> ConfirmAsync(
        AiScanUsageEstimate estimate,
        CancellationToken cancellationToken = default);
}
