namespace DriverUpdater.Core.Options;

public sealed class AppSettings
{
    public ApplicationSettings Application { get; set; } = new();
    public CatalogSettings Catalog { get; set; } = new();
    public BackupSettings Backup { get; set; } = new();
    public HistorySettings History { get; set; } = new();
    public ScheduleSettings Schedule { get; set; } = new();
    public LanguageSettings Language { get; set; } = new();
    public UpdaterSettings Updater { get; set; } = new();
    public ScraperSettings Scraper { get; set; } = new();
    public AiSettings Ai { get; set; } = new();
    public LogCleanupSettings LogCleanup { get; set; } = new();
    public OnboardingSettings Onboarding { get; set; } = new();
}
