using System.IO;
using System.Xml.Linq;
using FluentAssertions;

namespace DriverUpdater.App.Tests.Views;

public class MainWindowHeaderTests
{
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

    private static string ViewsFolder() => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", "..",
        "src", "DriverUpdater.App", "Views"));
}
