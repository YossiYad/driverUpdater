namespace DriverUpdater.Core.Options;

public sealed class ScraperSettings
{
    public const string SectionName = "Scraper";

    public bool EnablePlaywrightFallback { get; set; }
}
