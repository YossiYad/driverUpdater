using DriverUpdater.App;
using DriverUpdater.App.Logging;
using DriverUpdater.App.Services;
using DriverUpdater.App.ViewModels;
using DriverUpdater.Core.Abstractions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DriverUpdater.App.Tests.Integration;

// Builds the exact service graph the app composes at startup (AddDriverUpdaterApp) and
// resolves the top-level roots, so a missing or broken registration fails here instead
// of crashing on a friend's machine the first time they launch the app.
public class AppCompositionTests
{
    [WpfFact]
    public void Container_resolves_the_full_app_startup_graph()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.AddDriverUpdaterApp(new InMemoryLogSink());

        using var provider = services.BuildServiceProvider(validateScopes: true);

        provider.GetRequiredService<MainViewModel>().Should().NotBeNull();
        provider.GetRequiredService<HistoryViewModel>().Should().NotBeNull();
        provider.GetRequiredService<SettingsViewModel>().Should().NotBeNull();
        provider.GetRequiredService<LogsViewModel>().Should().NotBeNull();
        provider.GetRequiredService<IAppUpdater>().Should().NotBeNull();
        provider.GetRequiredService<IAiResultTranslator>().Should().NotBeNull();
        provider.GetRequiredService<IExternalLinkOpener>().Should().NotBeNull();
        provider.GetRequiredService<ISupportWindowOpener>().Should().NotBeNull();
        provider.GetRequiredService<ILogCleanupService>().Should().NotBeNull();
        provider.GetServices<IHostedService>()
            .Should().Contain(service => service is LogCleanupBackgroundService);
        provider.GetRequiredService<IHistoryRepository>().Should().NotBeNull();
        provider.GetRequiredService<IScheduledScanRunner>().Should().NotBeNull();
        // The scan turns vendor pages into installable packages through this service, and
        // hands the user the page itself when it cannot. Both are optional constructor
        // parameters on MainViewModel, so a missing registration would silently disable them.
        provider.GetRequiredService<IVendorPageInstallerResolver>().Should().NotBeNull();
        // Optional on both MainViewModel and ScheduledScanRunner: without the registration the
        // per-driver automatic update choice would silently stop being saved and honoured.
        provider.GetRequiredService<IAutoUpdateSelectionStore>().Should().NotBeNull();
        // Settings > Schedule > "Choose the drivers..." is the only way to edit that selection.
        provider.GetRequiredService<IAutoUpdateSelectionWindowOpener>().Should().NotBeNull();
        provider.GetRequiredService<AutoUpdateSelectionViewModel>().Should().NotBeNull();
        // Optional on both MainViewModel and ScheduledScanRunner: without the registration a
        // driver the user excluded would silently start being updated again.
        provider.GetRequiredService<IDriverUpdateExclusionStore>().Should().NotBeNull();
        // Settings > Sources > "Choose the drivers to leave alone..." is the only way to edit it.
        provider.GetRequiredService<IExcludedDriverSelectionWindowOpener>().Should().NotBeNull();
        provider.GetRequiredService<ExcludedDriverSelectionViewModel>().Should().NotBeNull();
        // Optional on ScheduledScanRunner: without it, an AI-recommended schedule would quietly
        // install nothing instead of asking the AI what to install.
        provider.GetRequiredService<IAiAutoUpdateAdvisor>().Should().NotBeNull();
        provider.GetServices<IUpdateSource>().Should().NotBeEmpty();
    }
}
