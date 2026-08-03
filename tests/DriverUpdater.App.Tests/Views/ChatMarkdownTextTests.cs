using System.Linq;
using System.Windows.Controls;
using System.Windows.Documents;
using DriverUpdater.App.Views;
using FluentAssertions;

namespace DriverUpdater.App.Tests.Views;

public class ChatMarkdownTextTests
{
    [WpfFact]
    public void Bold_markers_become_a_semi_bold_run_without_the_asterisks()
    {
        var textBlock = new TextBlock();

        ChatMarkdownText.SetSource(textBlock, "Set **schedule** now.");

        var runs = textBlock.Inlines.OfType<Run>().ToArray();
        runs.Should().Contain(r => r.Text == "schedule" && r.FontWeight == System.Windows.FontWeights.SemiBold);
        textBlock.Inlines.OfType<Run>().Should().NotContain(r => r.Text.Contains('*'));
    }

    [WpfFact]
    public void Backtick_spans_become_a_monospace_run_without_the_backticks()
    {
        var textBlock = new TextBlock();

        ChatMarkdownText.SetSource(textBlock, "Values: `off`, `scan-only`.");

        var runs = textBlock.Inlines.OfType<Run>().ToArray();
        runs.Should().Contain(r => r.Text == "off" && r.FontFamily.Source == "Consolas");
        runs.Should().Contain(r => r.Text == "scan-only" && r.FontFamily.Source == "Consolas");
        runs.Should().NotContain(r => r.Text.Contains('`'));
    }

    [WpfFact]
    public void Bullet_lines_get_a_bullet_mark_and_a_blank_line_before_them()
    {
        var textBlock = new TextBlock();

        ChatMarkdownText.SetSource(textBlock, "Intro line.\n* First item\n* Second item");

        var runs = textBlock.Inlines.OfType<Run>().Select(r => r.Text).ToArray();
        runs.Should().Contain("• ");
        runs.Should().Contain("First item");
        runs.Should().Contain("Second item");

        var breakCount = textBlock.Inlines.OfType<LineBreak>().Count();
        // One break into the first bullet's blank line, one more for the blank line itself, one
        // into the second bullet, one more for its blank line.
        breakCount.Should().Be(4);
    }

    [WpfFact]
    public void Setting_a_new_source_replaces_the_previous_inlines()
    {
        var textBlock = new TextBlock();
        ChatMarkdownText.SetSource(textBlock, "**old**");

        ChatMarkdownText.SetSource(textBlock, "new text");

        textBlock.Inlines.OfType<Run>().Should().ContainSingle(r => r.Text == "new text");
    }

    [WpfFact]
    public void Plain_text_with_no_markdown_renders_unchanged()
    {
        var textBlock = new TextBlock();

        ChatMarkdownText.SetSource(textBlock, "Nothing special here.");

        textBlock.Inlines.OfType<Run>().Should().ContainSingle(r => r.Text == "Nothing special here.");
    }
}
