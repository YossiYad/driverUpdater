using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Xml.Linq;
using FluentAssertions;

namespace DriverUpdater.App.Tests.Views;

/// <summary>
/// The chip fade and drift only fail at runtime, so the style is parsed and actually hosted here.
/// A transform declared in a Style setter would be frozen with the style and throw when animated;
/// keeping it inside the control template is what makes it per-instance and animatable.
/// </summary>
public class ChatSuggestionChipStyleTests
{
    [WpfFact]
    public void The_chip_style_loads_and_animates_a_hosted_button()
    {
        var style = (Style)XamlReader.Parse(ChipStyleXaml());
        var button = new Button { Style = style, Content = "What should I update first?" };
        var window = new Window
        {
            Content = button,
            Width = 300,
            Height = 120,
            Left = -4000,
            Top = -4000,
            ShowInTaskbar = false
        };

        try
        {
            window.Show();
            Pump();

            var chipRoot = (FrameworkElement)button.Template.FindName("ChipRoot", button);
            chipRoot.Should().NotBeNull();
            // The keyframes start at 0 and the drift starts at -10, so the storyboard is running.
            chipRoot.Opacity.Should().BeLessThan(1);
            chipRoot.RenderTransform.Value.OffsetX.Should().BeLessThan(0);
        }
        finally
        {
            window.Close();
        }
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.ContextIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static string ChipStyleXaml()
    {
        var viewsFolder = Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "DriverUpdater.App", "Views");
        var document = XDocument.Load(Path.Combine(viewsFolder, "MainWindow.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        var style = document.Descendants(presentation + "Style")
            .Single(element => element.Attribute(x + "Key")?.Value == "ChatSuggestionChipStyle");
        var standalone = new XElement(style);
        standalone.Add(new XAttribute(XNamespace.Xmlns + "x", x.NamespaceName));
        standalone.SetAttributeValue(x + "Key", null);
        return standalone.ToString();
    }
}
