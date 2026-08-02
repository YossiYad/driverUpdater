using System.IO;
using System.Xml.Linq;
using FluentAssertions;

namespace DriverUpdater.App.Tests.Views;

// The Schedule tab is where the whole unattended feature is discovered or missed. These tests
// keep it scrollable, keep the choice a single plain-language list instead of two enum combo
// boxes, and keep the way into the driver picker and the AI setup.
public class SettingsScheduleTabTests
{
    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    [Fact]
    public void The_schedule_tab_scrolls_when_its_options_do_not_fit()
    {
        var scrollViewer = ScheduleTab().Element(Presentation + "ScrollViewer");

        scrollViewer.Should().NotBeNull("the tab is taller than the window once a schedule is picked");
        scrollViewer!.Attribute("VerticalScrollBarVisibility")?.Value.Should().Be("Auto");
    }

    [Fact]
    public void One_radio_list_covers_every_schedule_type()
    {
        var radios = ScheduleTab()
            .Descendants(Presentation + "RadioButton")
            .Where(radio => radio.Attribute("GroupName")?.Value == "ScheduleType")
            .ToArray();

        radios.Select(radio => radio.Attribute("IsChecked")?.Value).Should().Equal(
            "{Binding IsScheduleOff, Mode=TwoWay}",
            "{Binding IsScanOnlySchedule, Mode=TwoWay}",
            "{Binding IsGeneralSchedule, Mode=TwoWay}",
            "{Binding IsCustomSchedule, Mode=TwoWay}",
            "{Binding IsAiSchedule, Mode=TwoWay}");
        radios.Select(radio => radio.Attribute("Content")?.Value)
            .Should().OnlyContain(content =>
                !content!.Contains("AiRecommended", StringComparison.Ordinal)
                && !content.Contains("ScanAndUpdate", StringComparison.Ordinal));
    }

    [Fact]
    public void The_custom_schedule_opens_the_driver_picker()
    {
        ScheduleTab().Descendants(Presentation + "Button")
            .Should().Contain(button =>
                (string?)button.Attribute("Command") == "{Binding ChooseAutoUpdateDriversCommand}",
                "the Auto column is gone, so this button is the only way to pick drivers");
    }

    [Fact]
    public void The_ai_schedule_offers_its_risk_level_and_a_way_to_set_the_provider()
    {
        var tab = ScheduleTab();

        tab.Descendants(Presentation + "RadioButton")
            .Where(radio => radio.Attribute("GroupName")?.Value == "AiRiskTolerance")
            .Select(radio => radio.Attribute("IsChecked")?.Value)
            .Should().Equal(
                "{Binding AiInstallsSafeOnly, Mode=TwoWay}",
                "{Binding AiInstallsSafeAndCaution, Mode=TwoWay}");

        tab.Descendants(Presentation + "Button")
            .Should().Contain(button => (string?)button.Attribute("Click") == "OnOpenAiTab",
                "the warning about a missing provider must lead somewhere");
    }

    [Fact]
    public void The_timing_fields_are_hidden_while_nothing_is_scheduled()
    {
        var cadence = ScheduleTab()
            .Descendants(Presentation + "ComboBox")
            .Single(combo => combo.Attribute("ItemsSource")?.Value == "{Binding AvailableCadences}");

        cadence.Ancestors(Presentation + "StackPanel")
            .Select(panel => panel.Attribute("Visibility")?.Value)
            .Should().Contain("{Binding ShowScheduleTiming, Converter={StaticResource BooleanToVisibilityConverter}}");
    }

    private static XElement ScheduleTab()
    {
        var document = XDocument.Load(Path.Combine(ViewsFolder(), "SettingsWindow.xaml"));
        return document.Descendants(Presentation + "TabItem")
            .Single(tab => tab.Attribute("Header")?.Value == "Schedule");
    }

    private static string ViewsFolder() => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", "..",
        "src", "DriverUpdater.App", "Views"));
}
