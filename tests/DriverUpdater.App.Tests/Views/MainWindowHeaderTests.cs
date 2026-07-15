using System.IO;
using System.Xml.Linq;
using FluentAssertions;

namespace DriverUpdater.App.Tests.Views;

public class MainWindowHeaderTests
{
    [Fact]
    public void Driver_grid_keeps_only_five_user_facing_columns_at_stable_widths()
    {
        var document = XDocument.Load(Path.Combine(ViewsFolder(), "MainWindow.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        var grid = document.Descendants(presentation + "DataGrid")
            .Single(element => element.Attribute(x + "Name")?.Value == "DriversGrid");
        var columns = grid.Element(presentation + "DataGrid.Columns")!
            .Elements()
            .ToArray();
        var headers = columns
            .Select(column => column.Attribute("Header")?.Value)
            .ToArray();

        headers.Should().Equal(
            "{DynamicResource Grid.Device}",
            "{DynamicResource Grid.Status}",
            "{DynamicResource Grid.DriverVersion}",
            "{DynamicResource Grid.Update}",
            "{DynamicResource Grid.Ai}");
        columns.All(column =>
            int.TryParse(column.Attribute("MinWidth")?.Value, out var width) && width >= 112)
            .Should().BeTrue();
        grid.Attribute("CanUserReorderColumns")?.Value.Should().Be("False");
        grid.Attribute("CanUserResizeColumns")?.Value.Should().Be("False");
        grid.Attribute("ScrollViewer.HorizontalScrollBarVisibility")?.Value.Should().Be("Auto");
    }

    [Fact]
    public void Support_button_is_the_leftmost_header_action()
    {
        var document = XDocument.Load(Path.Combine(ViewsFolder(), "MainWindow.xaml"));
        XNamespace ui = "http://schemas.lepo.co/wpfui/2022/xaml";
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        var trailingContent = document.Descendants(ui + "TitleBar.TrailingContent")
            .Should().ContainSingle().Subject;
        var actions = trailingContent.Element(presentation + "StackPanel");
        actions.Should().NotBeNull();
        var commands = actions!.Elements(presentation + "Button")
            .Select(button => button.Attribute("Command")?.Value)
            .ToArray();

        actions!.Attribute("FlowDirection")?.Value.Should().Be("LeftToRight");
        commands.Should().Equal(
            "{Binding OpenSupportCommand}",
            "{Binding OpenHistoryCommand}",
            "{Binding OpenLogsCommand}",
            "{Binding OpenSettingsCommand}");
    }

    [Fact]
    public void Header_icon_font_is_scoped_to_glyphs_so_tooltips_use_a_text_font()
    {
        var document = XDocument.Load(Path.Combine(ViewsFolder(), "MainWindow.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        var style = document.Descendants(presentation + "Style")
            .Single(element => element.Attribute(x + "Key")?.Value == "HeaderIconButtonStyle");
        style.Elements(presentation + "Setter")
            .Select(setter => setter.Attribute("Property")?.Value)
            .Should().NotContain("FontFamily");

        var iconButtons = document.Descendants(presentation + "Button")
            .Where(button => button.Attribute("Style")?.Value == "{StaticResource HeaderIconButtonStyle}")
            .Where(button => button.Attribute("ToolTip")?.Value?.StartsWith("{DynamicResource Toolbar.", StringComparison.Ordinal) == true)
            .ToArray();
        iconButtons.Should().HaveCount(3);
        iconButtons.Select(button =>
                button.Element(presentation + "TextBlock")?.Attribute("FontFamily")?.Value)
            .Should().OnlyContain(fontFamily => fontFamily == "Segoe MDL2 Assets");
    }

    private static string ViewsFolder() => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", "..",
        "src", "DriverUpdater.App", "Views"));
}
