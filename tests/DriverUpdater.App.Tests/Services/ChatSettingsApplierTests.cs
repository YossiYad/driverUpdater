using System.IO;
using DriverUpdater.App.Ai;
using DriverUpdater.App.Services;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using DriverUpdater.Core.Options;
using DriverUpdater.Core.Results;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriverUpdater.App.Tests.Services;

public class ChatSettingsApplierTests
{
    [Fact]
    public async Task Apply_saves_the_change_and_reprograms_the_scheduled_task()
    {
        var store = new FakeSettingsStore();
        var scheduler = new FakeScheduler();
        var applier = NewApplier(store, scheduler);

        var result = await applier.ApplyAsync(Changes(("schedule", "update-ai"), ("schedule.cadence", "daily")));

        result.Succeeded.Should().BeTrue();
        result.Warning.Should().BeNull();
        store.Saved!.Schedule.Mode.Should().Be(ScheduleMode.ScanAndUpdate);
        store.Saved.Schedule.AutoUpdateScope.Should().Be(AutoUpdateScope.AiRecommended);
        store.Saved.Schedule.Cadence.Should().Be(ScheduleCadence.Daily);
        scheduler.LastMode.Should().Be(ScheduleMode.ScanAndUpdate);
        scheduler.LastCadence.Should().Be(ScheduleCadence.Daily);
    }

    [Fact]
    public async Task Apply_pushes_the_close_behavior_into_the_running_app()
    {
        var store = new FakeSettingsStore();
        var behavior = new ApplicationBehaviorState();
        var applier = NewApplier(store, new FakeScheduler(), behavior);

        await applier.ApplyAsync(Changes(("close-button", "exit")));

        store.Saved!.Application.CloseBehavior.Should().Be(WindowCloseBehavior.ExitApplication);
        behavior.CloseBehavior.Should().Be(WindowCloseBehavior.ExitApplication);
    }

    [Fact]
    public async Task Apply_updates_the_windows_startup_entry()
    {
        var startup = new FakeStartupService();
        var applier = NewApplier(new FakeSettingsStore(), new FakeScheduler(), startupService: startup);

        await applier.ApplyAsync(Changes(("start-with-windows", "on"), ("start-minimized", "on")));

        startup.StartWithWindows.Should().BeTrue();
        startup.StartMinimized.Should().BeTrue();
    }

    [Fact]
    public async Task Start_minimized_is_dropped_when_the_app_is_not_kept_running()
    {
        var store = new FakeSettingsStore();
        var applier = NewApplier(store, new FakeScheduler());

        await applier.ApplyAsync(Changes(
            ("start-with-windows", "on"),
            ("start-minimized", "on"),
            ("close-button", "exit")));

        store.Saved!.Application.StartMinimized.Should().BeFalse();
    }

    [Fact]
    public async Task A_failing_schedule_update_is_reported_as_a_warning_not_a_failure()
    {
        var store = new FakeSettingsStore
        {
            Current = new AppSettings
            {
                Schedule = new ScheduleSettings { Mode = ScheduleMode.Manual }
            }
        };
        var scheduler = new FakeScheduler { Fail = true };
        var applier = NewApplier(store, scheduler);

        var result = await applier.ApplyAsync(Changes(("schedule", "scan-only")));

        result.Succeeded.Should().BeTrue();
        result.Warning.Should().Contain("access denied");
        store.Saved!.Schedule.Mode.Should().Be(ScheduleMode.Manual);
    }

    [Fact]
    public async Task A_failing_save_leaves_the_scheduled_task_untouched()
    {
        var store = new FakeSettingsStore { ThrowOnSave = true };
        var scheduler = new FakeScheduler();
        var applier = NewApplier(store, scheduler);

        var result = await applier.ApplyAsync(Changes(("schedule", "scan-only")));

        result.Succeeded.Should().BeFalse();
        scheduler.WasCalled.Should().BeFalse();
    }

    [Fact]
    public async Task An_empty_change_list_is_refused()
    {
        var applier = NewApplier(new FakeSettingsStore(), new FakeScheduler());

        var result = await applier.ApplyAsync(Array.Empty<ChatSettingChange>());

        result.Succeeded.Should().BeFalse();
    }

    private static ChatSettingsApplier NewApplier(
        ISettingsStore store,
        ISchedulerService scheduler,
        ApplicationBehaviorState? behavior = null,
        IApplicationStartupService? startupService = null) =>
        new(store,
            scheduler,
            NullLogger<ChatSettingsApplier>.Instance,
            behavior,
            startupService);

    private static ChatSettingChange[] Changes(params (string Key, string Value)[] pairs) =>
        pairs.Select(pair =>
        {
            ChatSettingCatalog.TryResolve(pair.Key, pair.Value, out var change).Should().BeTrue();
            return change;
        }).ToArray();

    private sealed class FakeSettingsStore : ISettingsStore
    {
        public AppSettings Current { get; set; } = new();
        public AppSettings? Saved { get; private set; }
        public bool ThrowOnSave { get; init; }

        public string SettingsPath => "in-memory";

        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Current);

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            if (ThrowOnSave)
            {
                throw new IOException("settings.json is locked");
            }

            Saved = settings;
            Current = settings;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeScheduler : ISchedulerService
    {
        public bool Fail { get; init; }
        public bool WasCalled { get; private set; }
        public ScheduleMode LastMode { get; private set; }
        public ScheduleCadence LastCadence { get; private set; }

        public Task<Result<ScheduledTaskInfo?>> ApplyAsync(
            ScheduleMode mode,
            ScheduleCadence cadence,
            TimeOnly timeOfDay,
            DayOfWeek dayOfWeek,
            CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            LastMode = mode;
            LastCadence = cadence;
            return Task.FromResult(Fail
                ? Result<ScheduledTaskInfo?>.Failure("schedule.failed", "access denied")
                : Result<ScheduledTaskInfo?>.Success(null));
        }

        public Task<ScheduledTaskInfo?> GetCurrentAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<ScheduledTaskInfo?>(null);

        public Task RemoveAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeStartupService : IApplicationStartupService
    {
        public bool StartWithWindows { get; private set; }
        public bool StartMinimized { get; private set; }

        public Task ApplyAsync(bool startWithWindows, bool startMinimized, CancellationToken cancellationToken = default)
        {
            StartWithWindows = startWithWindows;
            StartMinimized = startMinimized;
            return Task.CompletedTask;
        }
    }
}
