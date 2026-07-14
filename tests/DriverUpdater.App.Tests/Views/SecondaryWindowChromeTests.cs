using System.IO;
using System.Xml.Linq;
using FluentAssertions;

namespace DriverUpdater.App.Tests.Views;

public class SecondaryWindowChromeTests
{
    private static readonly string[] WindowFiles =
    [
        "AiResultWindow.xaml",
        "HistoryWindow.xaml",
        "LogsWindow.xaml",
        "SettingsWindow.xaml",
        "UpdateSummaryWindow.xaml",
        "WelcomeWindow.xaml",
        Path.Combine("Dialogs", "ConfirmUpdateDialog.xaml")
    ];

    [Theory]
    [MemberData(nameof(SecondaryWindows))]
    public void Secondary_window_has_main_style_minimize_and_close_title_bar(string relativePath)
    {
        var document = XDocument.Load(Path.Combine(ViewsFolder(), relativePath));
        XNamespace ui = "http://schemas.lepo.co/wpfui/2022/xaml";

        var titleBar = document.Descendants(ui + "TitleBar").Should().ContainSingle().Subject;

        titleBar.Attribute("Height")?.Value.Should().Be("56");
        titleBar.Attribute("ShowMinimize")?.Value.Should().Be("True");
        titleBar.Attribute("ShowMaximize")?.Value.Should().Be("False");
        titleBar.Attribute("CanMaximize")?.Value.Should().Be("False");
        titleBar.Attribute("ShowClose")?.Value.Should().Be("True");
        titleBar.Attribute("ButtonsForeground")?.Value
            .Should().Be("{DynamicResource SystemControlForegroundBaseHighBrush}");
        titleBar.Attribute("ButtonsBackground")?.Value
            .Should().Be("{DynamicResource SystemControlBackgroundChromeMediumBrush}");
    }

    public static IEnumerable<object[]> SecondaryWindows() =>
        WindowFiles.Select(path => new object[] { path });

    private static string ViewsFolder() => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", "..",
        "src", "DriverUpdater.App", "Views"));
}
