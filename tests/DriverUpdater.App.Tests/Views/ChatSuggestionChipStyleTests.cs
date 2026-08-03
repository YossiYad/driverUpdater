using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
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

    [WpfFact]
    public void Swapping_a_chip_gives_it_a_fresh_container_that_fades_in_again()
    {
        var items = new ObservableCollection<string> { "first", "second", "third" };
        var control = (ItemsControl)XamlReader.Parse(ChipListXaml());
        control.ItemsSource = items;
        var window = new Window
        {
            Content = control,
            Width = 320,
            Height = 200,
            Left = -4000,
            Top = -4000,
            ShowInTaskbar = false
        };

        try
        {
            window.Show();
            Pump();
            var before = control.ItemContainerGenerator.ContainerFromIndex(0);

            // What the rotation does. Assigning over the slot instead would keep this container,
            // and the replacement chip would inherit the finished fade-out and stay invisible.
            items.RemoveAt(0);
            items.Insert(0, "fourth");
            Pump();

            var after = control.ItemContainerGenerator.ContainerFromIndex(0);
            after.Should().NotBeSameAs(before);

            var button = FindButton((DependencyObject)after);
            button.Should().NotBeNull();
            var chipRoot = (FrameworkElement)button!.Template.FindName("ChipRoot", button);
            chipRoot.Opacity.Should().BeLessThan(1);
            chipRoot.RenderTransform.Value.OffsetX.Should().BeLessThan(0);
        }
        finally
        {
            window.Close();
        }
    }

    private static Button? FindButton(DependencyObject root)
    {
        if (root is Button button)
        {
            return button;
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var found = FindButton(VisualTreeHelper.GetChild(root, i));
            if (found is not null)
            {
                return found;
            }
        }

        return null;
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
        var standalone = ChipStyleElement();
        standalone.SetAttributeValue(XName.Get("Key", Xaml.NamespaceName), null);
        return standalone.ToString();
    }

    private static string ChipListXaml()
    {
        var list = new XElement(
            Presentation + "ItemsControl",
            new XAttribute(XNamespace.Xmlns + "x", Xaml.NamespaceName),
            new XElement(Presentation + "ItemsControl.Resources", ChipStyleElement()),
            new XElement(
                Presentation + "ItemsControl.ItemTemplate",
                new XElement(
                    Presentation + "DataTemplate",
                    new XElement(
                        Presentation + "Button",
                        new XAttribute("Style", "{StaticResource ChatSuggestionChipStyle}"),
                        new XAttribute("Content", "{Binding}")))));
        return list.ToString();
    }

    private static XElement ChipStyleElement()
    {
        var viewsFolder = Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "DriverUpdater.App", "Views");
        var document = XDocument.Load(Path.Combine(viewsFolder, "MainWindow.xaml"));

        var style = document.Descendants(Presentation + "Style")
            .Single(element => element.Attribute(Xaml + "Key")?.Value == "ChatSuggestionChipStyle");
        var standalone = new XElement(style);
        standalone.Add(new XAttribute(XNamespace.Xmlns + "x", Xaml.NamespaceName));
        return standalone;
    }

    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
}
