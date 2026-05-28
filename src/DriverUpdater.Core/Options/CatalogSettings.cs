namespace DriverUpdater.Core.Options;

public sealed class CatalogSettings
{
    public const string SectionName = "Catalog";

    public bool Enabled { get; set; } = true;

    public int MaxConcurrentSearches { get; set; } = 4;

    public TimeSpan CacheDuration { get; set; } = TimeSpan.FromHours(24);

    public int MaxRetries { get; set; } = 3;

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);
}
