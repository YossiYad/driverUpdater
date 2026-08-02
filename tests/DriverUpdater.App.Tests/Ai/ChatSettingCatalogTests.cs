using DriverUpdater.App.Ai;
using DriverUpdater.Core.Models;
using DriverUpdater.Core.Options;
using FluentAssertions;

namespace DriverUpdater.App.Tests.Ai;

public class ChatSettingCatalogTests
{
    [Theory]
    [InlineData("off", ScheduleMode.Manual, AutoUpdateScope.AllDrivers)]
    [InlineData("scan-only", ScheduleMode.ScanOnly, AutoUpdateScope.AllDrivers)]
    [InlineData("update-all", ScheduleMode.ScanAndUpdate, AutoUpdateScope.AllDrivers)]
    [InlineData("update-selected", ScheduleMode.ScanAndUpdate, AutoUpdateScope.SelectedDrivers)]
    [InlineData("update-ai", ScheduleMode.ScanAndUpdate, AutoUpdateScope.AiRecommended)]
    public void Schedule_writes_the_matching_mode_and_scope(
        string value,
        ScheduleMode expectedMode,
        AutoUpdateScope expectedScope)
    {
        ChatSettingCatalog.TryResolve("schedule", value, out var change).Should().BeTrue();
        var settings = new AppSettings();

        change.Definition.TryWrite(settings, change.Value).Should().BeTrue();

        settings.Schedule.Mode.Should().Be(expectedMode);
        if (expectedMode == ScheduleMode.ScanAndUpdate)
        {
            settings.Schedule.AutoUpdateScope.Should().Be(expectedScope);
        }
    }

    [Fact]
    public void Close_button_maps_to_the_window_close_behavior()
    {
        ChatSettingCatalog.TryResolve("close-button", "background", out var change).Should().BeTrue();
        var settings = new AppSettings();

        change.Definition.TryWrite(settings, change.Value).Should().BeTrue();

        settings.Application.CloseBehavior.Should().Be(WindowCloseBehavior.KeepRunningInBackground);
    }

    [Fact]
    public void Unknown_key_is_rejected()
    {
        ChatSettingCatalog.TryResolve("format-the-disk", "on", out _).Should().BeFalse();
    }

    [Fact]
    public void Value_outside_the_allowed_set_is_rejected()
    {
        ChatSettingCatalog.TryResolve("close-button", "explode", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("22:00", true)]
    [InlineData("9:30", true)]
    [InlineData("25:00", false)]
    [InlineData("evening", false)]
    public void Schedule_time_only_accepts_a_valid_clock_time(string value, bool expected)
    {
        ChatSettingCatalog.TryResolve("schedule.time", value, out _).Should().Be(expected);
    }

    [Theory]
    [InlineData("14", true)]
    [InlineData("0", false)]
    [InlineData("400", false)]
    [InlineData("many", false)]
    public void Log_retention_stays_inside_the_supported_range(string value, bool expected)
    {
        ChatSettingCatalog.TryResolve("logs.retention-days", value, out _).Should().Be(expected);
    }

    [Fact]
    public void Empty_key_or_value_is_rejected()
    {
        ChatSettingCatalog.TryResolve(null, "on", out _).Should().BeFalse();
        ChatSettingCatalog.TryResolve("close-button", "  ", out _).Should().BeFalse();
    }

    [Fact]
    public void Current_values_are_described_for_every_key()
    {
        var described = ChatSettingCatalog.DescribeCurrent(new AppSettings());

        described.Should().HaveCount(ChatSettingCatalog.All.Count);
        described.Should().Contain("schedule = off");
        described.Should().Contain("close-button = background");
    }

    [Fact]
    public void Changes_are_described_in_both_languages()
    {
        ChatSettingCatalog.TryResolve("schedule", "off", out var change).Should().BeTrue();

        change.Describe(AppLanguage.English).Should().Be("Turn the scheduled driver run off.");
        change.Describe(AppLanguage.Hebrew).Should().Be("לכבות את הסריקה המתוזמנת.");
    }

    [Fact]
    public void Every_listed_value_can_actually_be_written()
    {
        foreach (var definition in ChatSettingCatalog.All)
        {
            if (definition.AllowedValues[0].StartsWith('<'))
            {
                continue;
            }

            foreach (var value in definition.AllowedValues)
            {
                ChatSettingCatalog.TryResolve(definition.Key, value, out var change)
                    .Should().BeTrue($"{definition.Key}={value} is advertised to the AI");
                change.Definition.TryWrite(new AppSettings(), change.Value).Should().BeTrue();
            }
        }
    }
}
