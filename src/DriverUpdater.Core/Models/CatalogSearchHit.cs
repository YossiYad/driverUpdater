namespace DriverUpdater.Core.Models;

public sealed record CatalogSearchHit(
    string UpdateId,
    string Title,
    string? Products,
    string? Classification,
    string? LastUpdatedText,
    DateOnly? LastUpdatedDate,
    string? VersionText,
    Version? Version,
    string? SizeText,
    long? SizeBytes);
