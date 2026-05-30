namespace DriverUpdater.Core.Models;

public sealed record AiVerificationRequest(
    string CorrelationId,
    string DeviceName,
    string HardwareId,
    string? InstalledVersion,
    DateOnly? InstalledDate,
    string CandidateVersion,
    DateOnly CandidateDate,
    UpdateSource Source,
    string DownloadUrl);
