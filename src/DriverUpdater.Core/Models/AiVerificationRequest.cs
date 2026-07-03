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
    string DownloadUrl,
    DriverCategory Category = DriverCategory.Other,
    string Provider = "",
    string Manufacturer = "",
    UpdateInstallKind InstallKind = UpdateInstallKind.WindowsUpdate,
    UpdateConfidence Confidence = UpdateConfidence.Confirmed,
    bool FindLatestWhenNoCandidate = false);
