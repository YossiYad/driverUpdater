namespace DriverUpdater.Core.Options;

public sealed class AppSettings
{
    public CatalogSettings Catalog { get; set; } = new();
}
