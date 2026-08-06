using DriverUpdater.App.Services;
using DriverUpdater.Core.Options;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Velopack;

namespace DriverUpdater.App.Tests.Services;

public class VelopackAppUpdaterTests
{
    [Fact]
    public async Task CheckForUpdatesAsync_returns_none_when_no_feed_configured()
    {
        var settings = new UpdaterSettings { GitHubRepoUrl = null, FeedUrl = null };
        var updater = NewUpdater(settings);

        var result = await updater.CheckForUpdatesAsync();

        result.IsUpdateAvailable.Should().BeFalse();
        result.Status.Should().Be(AppUpdateCheckStatus.NotConfigured);
        result.Version.Should().BeNull();
    }

    [Fact]
    public async Task CheckForUpdatesAsync_returns_none_when_not_installed_via_velopack()
    {
        // The GitHub repo is configured but the test host is not a Velopack install, so the
        // updater must report no update rather than attempt to download anything.
        VelopackApp.Build().Run();
        var settings = new UpdaterSettings { GitHubRepoUrl = "https://github.com/YossiYad/driverUpdater" };
        var updater = NewUpdater(settings);

        var result = await updater.CheckForUpdatesAsync();

        result.IsUpdateAvailable.Should().BeFalse();
        result.Status.Should().Be(AppUpdateCheckStatus.NotInstalled);
    }

    private static VelopackAppUpdater NewUpdater(UpdaterSettings settings) =>
        new(new ConstantOptionsMonitor<UpdaterSettings>(settings), NullLogger<VelopackAppUpdater>.Instance);

    private sealed class ConstantOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public ConstantOptionsMonitor(T value) { CurrentValue = value; }
        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string> listener) => null;
    }
}
