namespace DriverUpdater.Core.Options;

public sealed class UpdaterSettings
{
    public const string SectionName = "Updater";

    public bool CheckOnStartup { get; set; }

    public string? FeedUrl { get; set; }

    // GitHub repository the app self-updates from (Velopack GithubSource). When set it
    // takes precedence over FeedUrl. Releases must be published as GitHub releases whose
    // assets include the Velopack files (RELEASES, *.nupkg) produced by `vpk pack`.
    public string? GitHubRepoUrl { get; set; } = "https://github.com/YossiYad/driverUpdater";

    // When true, pre-release GitHub releases are also considered as updates.
    public bool AllowPrerelease { get; set; }

    public bool AutoApply { get; set; }

    public bool WindowsUpdateEnabled { get; set; } = true;

    public bool OemSourcesEnabled { get; set; } = true;
}
