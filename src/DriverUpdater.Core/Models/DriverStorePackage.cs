namespace DriverUpdater.Core.Models;

/// <summary>One third-party driver package currently present in the Windows DriverStore.</summary>
public sealed record DriverStorePackage(
    string PublishedName,
    string? OriginalFileName,
    string? Provider,
    string? ClassName,
    string? Version,
    DateOnly? Date);
