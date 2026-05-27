namespace DriverUpdater.Core.Models;

public sealed record WuDriverUpdateRecord(
    string UpdateId,
    int RevisionNumber,
    string Title,
    string? DriverHardwareId,
    string? DriverModel,
    string? DriverManufacturer,
    string? DriverProvider,
    DateOnly? DriverVerDate,
    long MaxDownloadSize,
    string? DownloadUrl,
    IReadOnlyList<string> KbArticleIds);
