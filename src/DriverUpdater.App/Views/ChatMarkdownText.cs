using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace DriverUpdater.App.Views;

/// <summary>
/// Renders the light markdown the AI chat replies use (**bold**, `code`, "* " bullet lines) as
/// TextBlock inlines instead of showing the raw asterisks and backticks. The model is only ever
/// asked for this small subset, so a full markdown parser would be solving a problem nobody has.
/// </summary>
public static class ChatMarkdownText
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.RegisterAttached(
        "Source",
        typeof(string),
        typeof(ChatMarkdownText),
        new PropertyMetadata(null, OnSourceChanged));

    public static void SetSource(DependencyObject element, string? value) => element.SetValue(SourceProperty, value);

    public static string? GetSource(DependencyObject element) => (string?)element.GetValue(SourceProperty);

    private static readonly Regex TokenPattern = new(
        @"\*\*(?<bold>.+?)\*\*|`(?<code>[^`]+?)`",
        RegexOptions.Compiled);

    private static readonly Brush BulletBrush = new SolidColorBrush(Color.FromRgb(0x8B, 0x5C, 0xF6));
    private static readonly Brush CodeBackground = new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80));

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock textBlock)
        {
            return;
        }

        textBlock.Inlines.Clear();
        if (e.NewValue is not string text || text.Length == 0)
        {
            return;
        }

        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();
            var isBullet = trimmed.StartsWith("* ", StringComparison.Ordinal)
                || trimmed.StartsWith("- ", StringComparison.Ordinal);

            if (i > 0)
            {
                textBlock.Inlines.Add(new LineBreak());
                if (isBullet)
                {
                    textBlock.Inlines.Add(new LineBreak());
                }
            }

            if (isBullet)
            {
                textBlock.Inlines.Add(new Run("• ") { Foreground = BulletBrush, FontWeight = FontWeights.Bold });
                AppendFormattedRuns(textBlock.Inlines, trimmed[2..]);
            }
            else
            {
                AppendFormattedRuns(textBlock.Inlines, line);
            }
        }
    }

    private static void AppendFormattedRuns(InlineCollection inlines, string segment)
    {
        var lastIndex = 0;
        foreach (Match match in TokenPattern.Matches(segment))
        {
            if (match.Index > lastIndex)
            {
                inlines.Add(new Run(segment[lastIndex..match.Index]));
            }

            if (match.Groups["bold"].Success)
            {
                inlines.Add(new Run(match.Groups["bold"].Value) { FontWeight = FontWeights.SemiBold });
            }
            else if (match.Groups["code"].Success)
            {
                inlines.Add(new Run(match.Groups["code"].Value)
                {
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 11,
                    Background = CodeBackground
                });
            }

            lastIndex = match.Index + match.Length;
        }

        if (lastIndex < segment.Length)
        {
            inlines.Add(new Run(segment[lastIndex..]));
        }
    }
}
