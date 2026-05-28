namespace DriverUpdater.Core.Options;

public sealed class BackupSettings
{
    public const string SectionName = "Backup";

    public string? RootPath { get; set; }

    public int RetentionDays { get; set; } = 30;
}
