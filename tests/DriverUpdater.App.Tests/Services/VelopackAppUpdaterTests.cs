using DriverUpdater.App.Services;
using DriverUpdater.Core.Options;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DriverUpdater.App.Tests.Services;

public class VelopackAppUpdaterTests
{
    [Fact]
    public async Task CheckAndApplyAsync_does_nothing_when_disabled()
    {
        var settings = new UpdaterSettings { CheckOnStartup = false, FeedUrl = null };
        var updater = NewUpdater(settings);

        Func<Task> act = () => updater.CheckAndApplyAsync();

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task CheckAndApplyAsync_does_nothing_when_feed_url_is_blank()
    {
        var settings = new UpdaterSettings { CheckOnStartup = true, FeedUrl = " " };
        var updater = NewUpdater(settings);

        Func<Task> act = () => updater.CheckAndApplyAsync();

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task CheckAndApplyAsync_swallows_errors_from_invalid_feed()
    {
        var settings = new UpdaterSettings
        {
            CheckOnStartup = true,
            FeedUrl = "https://invalid-host-does-not-exist.example.invalid/feed"
        };
        var updater = NewUpdater(settings);

        Func<Task> act = () => updater.CheckAndApplyAsync();

        await act.Should().NotThrowAsync();
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
