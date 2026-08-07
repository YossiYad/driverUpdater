using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using DriverUpdater.App.ViewModels;
using DriverUpdater.App.Views;
using DriverUpdater.Core.Models;
using FluentAssertions;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace DriverUpdater.App.Tests.Views;

/// <summary>
/// Every "Ask AI" busy bar must sweep from the same clock. Per-row indeterminate ProgressBars used
/// to drift apart because each one started its own animation, so the real cell template is hosted
/// here and the rendered offsets are compared while rows start checking at different moments.
/// </summary>
public class AiSweepClockTests
{
    [WpfFact]
    public void Rows_that_start_checking_at_different_times_sweep_at_the_same_offset()
    {
        var first = NewRow("First adapter");
        var second = NewRow("Second adapter");
        var rows = new ObservableCollection<DriverRowViewModel> { first, second };
        var list = NewAiCellList(rows);
        var window = NewHostWindow(list);

        try
        {
            window.Show();
            Pump();

            first.IsAiChecking = true;
            Pump();
            Advance();

            var firstOffsetAlone = SweepOffsets(list).Single();
            firstOffsetAlone.Should().BeGreaterThan(-AiSweepClock.SweepWidth, "the shared clock starts with the first bar");

            second.IsAiChecking = true;
            Pump();
            Advance();

            var offsets = SweepOffsets(list);
            offsets.Should().HaveCount(2);
            offsets.Distinct().Should().HaveCount(1, "a bar that appears later must join the sweep already in progress");
        }
        finally
        {
            first.IsAiChecking = false;
            second.IsAiChecking = false;
            window.Close();
        }
    }

    [WpfFact]
    public void The_sweep_stops_and_resets_when_no_row_is_checking()
    {
        var row = NewRow("Only adapter");
        var rows = new ObservableCollection<DriverRowViewModel> { row };
        var list = NewAiCellList(rows);
        var window = NewHostWindow(list);

        try
        {
            window.Show();
            Pump();

            row.IsAiChecking = true;
            Pump();
            Advance();
            SweepOffsets(list).Single().Should().BeGreaterThan(-AiSweepClock.SweepWidth);

            row.IsAiChecking = false;
            Pump();
            Advance();

            AiSweepClock.Instance.Offset.Should().Be(-AiSweepClock.SweepWidth);
        }
        finally
        {
            row.IsAiChecking = false;
            window.Close();
        }
    }

    private static IReadOnlyList<double> SweepOffsets(ItemsControl list)
    {
        var offsets = new List<double>();
        CollectVisibleSweeps(list, offsets);
        return offsets;
    }

    private static void CollectVisibleSweeps(DependencyObject root, List<double> offsets)
    {
        if (root is Rectangle rectangle && rectangle.IsVisible)
        {
            offsets.Add(rectangle.RenderTransform.Value.OffsetX);
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            CollectVisibleSweeps(VisualTreeHelper.GetChild(root, i), offsets);
        }
    }

    private static ItemsControl NewAiCellList(ObservableCollection<DriverRowViewModel> rows)
    {
        return new ItemsControl
        {
            ItemTemplate = (DataTemplate)XamlReader.Parse(AiCellTemplateXaml()),
            ItemsSource = rows
        };
    }

    private static Window NewHostWindow(ItemsControl list) => new()
    {
        Content = list,
        Width = 400,
        Height = 200,
        Left = -4000,
        Top = -4000,
        ShowInTaskbar = false
    };

    private static string AiCellTemplateXaml()
    {
        var viewsFolder = Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "DriverUpdater.App", "Views");
        var document = XDocument.Load(Path.Combine(viewsFolder, "MainWindow.xaml"));

        var column = document.Descendants(Presentation + "DataGridTemplateColumn")
            .Single(element => element.Attribute("Header")?.Value == "{DynamicResource Grid.Ai}");
        var template = new XElement(column
            .Element(Presentation + "DataGridTemplateColumn.CellTemplate")!
            .Element(Presentation + "DataTemplate")!);

        // MainWindow maps the views prefix without an assembly, which only resolves inside the app
        // assembly. Parsing it from the test assembly needs the assembly-qualified mapping.
        foreach (var element in template.DescendantsAndSelf().ToArray())
        {
            foreach (var attribute in element.Attributes().Where(a => a.Name.Namespace == AppViews).ToArray())
            {
                attribute.Remove();
                element.SetAttributeValue(QualifiedAppViews + attribute.Name.LocalName, attribute.Value);
            }
        }

        template.SetAttributeValue(XNamespace.Xmlns + "x", Xaml.NamespaceName);
        template.SetAttributeValue(XNamespace.Xmlns + "views", QualifiedAppViews.NamespaceName);
        template.AddFirst(new XElement(
            Presentation + "DataTemplate.Resources",
            new XElement(
                Presentation + "BooleanToVisibilityConverter",
                new XAttribute(Xaml + "Key", "BooleanToVisibilityConverter")),
            new XElement(
                Presentation + "SolidColorBrush",
                new XAttribute(Xaml + "Key", "AiSweepBrush"),
                new XAttribute("Color", "Green"))));
        return template.ToString();
    }

    private static void Advance()
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(120);
        while (DateTime.UtcNow < deadline)
        {
            Pump();
        }

        Pump();
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.ContextIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static DriverRowViewModel NewRow(string deviceName) => new(new DriverInfo(
        DeviceId: $"PCI\\VEN_8086&DEV_1234\\{deviceName}",
        HardwareId: "PCI\\VEN_8086&DEV_1234",
        DeviceName: deviceName,
        Category: DriverCategory.Network,
        Provider: "Intel",
        Manufacturer: "Intel Corporation",
        CurrentVersion: new Version(1, 2, 3, 4),
        CurrentDate: new DateOnly(2024, 3, 6),
        InfName: "oem1.inf",
        InfPath: "C:\\Windows\\INF\\oem1.inf",
        IsSigned: true,
        DeviceClass: "Net"));

    private static readonly XNamespace AppViews = "clr-namespace:DriverUpdater.App.Views";

    private static readonly XNamespace QualifiedAppViews =
        "clr-namespace:DriverUpdater.App.Views;assembly=DriverUpdater";

    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
}
