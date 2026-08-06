using DriverUpdater.Core.Models;
using DriverUpdater.Core.Options;
using FluentAssertions;

namespace DriverUpdater.Core.Tests.Options;

public class OptionDefaultsTests
{
    [Fact]
    public void Ai_defaults_keep_the_provider_off_but_preserve_safe_user_facing_features()
    {
        var settings = new AiSettings();

        settings.Provider.Should().Be(AiProvider.Off);
        settings.ResponseLanguage.Should().Be(AppLanguage.English);
        settings.EnableWebSearch.Should().BeTrue();
        settings.ShowAiScanUsageWarning.Should().BeTrue();
        settings.GeminiModel.Should().Be("gemini-2.5-flash");
        settings.OllamaBaseUrl.Should().Be("http://localhost:11434");
        settings.OllamaModel.Should().Be("llama3.1");
    }

    [Fact]
    public void Log_cleanup_defaults_are_enabled_with_the_documented_retention()
    {
        var settings = new LogCleanupSettings();

        settings.Enabled.Should().BeTrue();
        settings.RetentionDays.Should().Be(LogCleanupSettings.DefaultRetentionDays);
    }

    [Fact]
    public void Updater_defaults_use_the_project_repository_and_enable_both_driver_sources()
    {
        var settings = new UpdaterSettings();

        settings.GitHubRepoUrl.Should().Be("https://github.com/YossiYad/driverUpdater");
        settings.WindowsUpdateEnabled.Should().BeTrue();
        settings.OemSourcesEnabled.Should().BeTrue();
    }
}
