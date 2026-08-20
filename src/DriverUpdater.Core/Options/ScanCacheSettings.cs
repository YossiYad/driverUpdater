namespace DriverUpdater.Core.Options;

public sealed class ScanCacheSettings
{
    public const string SectionName = "ScanCache";
    public const int DefaultRetentionHours = 24;
    public const int MinimumRetentionHours = 1;
    public const int MaximumRetentionHours = 8760;

    public bool ExpirationEnabled { get; set; } = true;
    public int RetentionHours { get; set; } = DefaultRetentionHours;
}
