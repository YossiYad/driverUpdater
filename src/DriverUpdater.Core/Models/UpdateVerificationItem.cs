namespace DriverUpdater.Core.Models;

public sealed record UpdateVerificationItem(
    Guid OperationId,
    string DeviceName,
    DriverCategory Category,
    Version? PreviousVersion,
    DateOnly? PreviousDate,
    Version? ExpectedVersion,
    DateOnly? ExpectedDate,
    Version? CurrentVersion,
    DateOnly? CurrentDate,
    UpdateVerificationStatus Status,
    string? TechnicalDetail,
    UpdateStatus InstallerStatus,
    UpdateInstallKind InstallKind,
    UpdateConfidence Confidence,
    Uri? ActionUrl);
