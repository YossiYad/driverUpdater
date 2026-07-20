namespace DriverUpdater.Core.Options;

public sealed class LogCleanupSettings
{
    public const string SectionName = "LogCleanup";
    public const int DefaultRetentionDays = 7;
    public const int MinimumRetentionDays = 1;
    public const int MaximumRetentionDays = 365;

    public bool Enabled { get; set; } = true;
    public int RetentionDays { get; set; } = DefaultRetentionDays;
}
