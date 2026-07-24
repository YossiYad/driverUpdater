namespace DriverUpdater.App.Services;

public sealed record AiScanUsageEstimate(
    int DriverCount,
    int PlannedRequests,
    string Model);

public interface IAiScanConfirmation
{
    Task<bool> ConfirmAsync(
        AiScanUsageEstimate estimate,
        CancellationToken cancellationToken = default);
}
