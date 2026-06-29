namespace DriverUpdater.Core.Options;

public sealed class UpdaterSettings
{
    public const string SectionName = "Updater";

    public bool CheckOnStartup { get; set; }

    public string? FeedUrl { get; set; }

    public bool AutoApply { get; set; }

    public bool WindowsUpdateEnabled { get; set; } = true;

    public bool OemSourcesEnabled { get; set; } = true;
}
