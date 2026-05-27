namespace DriverUpdater.Core.Models;

public sealed record CatalogDownloadInfo(
    string UpdateId,
    Uri DownloadUrl,
    long? SizeBytes);
