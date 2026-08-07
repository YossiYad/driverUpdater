using System.IO;
using System.Xml.Linq;
using FluentAssertions;

namespace DriverUpdater.App.Tests.Views;

// The guide is the only place a returning user is told what changed, and it ships in two
// languages that are maintained by hand. These tests keep the English and Hebrew panels from
// drifting apart: both carry the badge, the pointer to the bottom of the guide, and the card
// the pointer promises.
public class WelcomeWindowNewFeaturesTests
{
    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Theory]
    [InlineData("EnglishPanel")]
    [InlineData("HebrewPanel")]
    public void Each_language_panel_opens_with_a_new_features_badge(string panelName)
    {
        var badges = Panel(panelName)
            .Descendants(Presentation + "Border")
            .Where(border => border.Attribute("Style")?.Value == "{StaticResource WelcomeNewBadgeStyle}")
            .ToArray();

        badges.Should().ContainSingle();
        badges[0].Descendants(Presentation + "Run")
            .Select(run => run.Attribute("Text")?.Value)
            .Should().Contain("{DynamicResource App.Version}",
                "the badge must follow the running version instead of a hand-edited number");
    }

    [Theory]
    [InlineData("EnglishPanel")]
    [InlineData("HebrewPanel")]
    public void Each_language_panel_points_at_the_new_features_card(string panelName)
    {
        var panel = Panel(panelName);

        var subtitle = panel.Elements(Presentation + "TextBlock")
            .Should().ContainSingle(block => (string?)block.Attribute("Foreground") == "#FFC4B5FD")
            .Subject;
        ((string?)subtitle.Attribute("Text")).Should().NotBeNullOrWhiteSpace();

        var cards = panel.Elements(Presentation + "Border")
            .Where(border => border.Attribute("Style")?.Value == "{StaticResource WelcomeNewCardStyle}")
            .ToArray();
        cards.Should().ContainSingle();
        panel.Elements().ToList().IndexOf(cards[0])
            .Should().BeGreaterThan(panel.Elements().ToList().IndexOf(subtitle),
                "the subtitle says the new features are further down the guide");
    }

    [Theory]
    [InlineData("EnglishPanel")]
    [InlineData("HebrewPanel")]
    public void The_new_features_card_lists_every_change_of_this_release(string panelName)
    {
        var card = Panel(panelName)
            .Elements(Presentation + "Border")
            .Single(border => border.Attribute("Style")?.Value == "{StaticResource WelcomeNewCardStyle}");

        var expectedHeadings = panelName == "EnglishPanel"
            ? new[]
            {
                "Ask the AI to change your settings: ",
                "Ask what is turned on right now: ",
                "Conversation starters: ",
                "One list of schedule types in Settings > Schedule: ",
                "The AI can decide what a scheduled run installs: ",
                "Your own list of drivers: ",
                "Drivers you never want touched: ",
                "The close button keeps DriverUpdater running: ",
                "The AI scan indicator moves as one: "
            }
            : new[]
            {
                "אפשר לבקש מה־AI לשנות הגדרות: ",
                "אפשר לשאול מה מוגדר כרגע: ",
                "כפתורי פתיחת שיחה: ",
                "רשימה אחת של סוגי תזמון בהגדרות, בלשונית Schedule: ",
                "ה־AI מחליט מה יותקן בסריקה המתוזמנת: ",
                "רשימת מנהלי ההתקנים שלכם: ",
                "מנהלי התקנים שלא רוצים לעדכן בכלל: ",
                "כפתור הסגירה משאיר את DriverUpdater פעילה: ",
                "מחוון סריקת ה־AI זז יחד: "
            };

        card.Descendants(Presentation + "Run")
            .Where(run => run.Attribute("FontWeight")?.Value == "SemiBold")
            .Select(run => run.Attribute("Text")?.Value)
            .Should().Equal(expectedHeadings,
                "each language must describe the actual release features, not merely contain the right count");
    }

    [Theory]
    [InlineData("EnglishPanel", "EnglishNewCard")]
    [InlineData("HebrewPanel", "HebrewNewCard")]
    public void Each_language_panel_offers_a_shortcut_down_to_the_new_features_card(
        string panelName,
        string cardName)
    {
        var panel = Panel(panelName);

        var jumpButtons = panel.Elements(Presentation + "Button")
            .Where(button => button.Attribute("Click")?.Value == "OnShowNewFeatures")
            .ToArray();
        jumpButtons.Should().ContainSingle();
        ((string?)jumpButtons[0].Attribute("Content")).Should().NotBeNullOrWhiteSpace();

        // The handler scrolls by name, so the card has to keep carrying it.
        panel.Elements(Presentation + "Border")
            .Should().ContainSingle(border => (string?)border.Attribute(X + "Name") == cardName);
    }

    private static XElement Panel(string panelName)
    {
        var document = XDocument.Load(Path.Combine(ViewsFolder(), "WelcomeWindow.xaml"));
        return document.Descendants(Presentation + "StackPanel")
            .Single(element => element.Attribute(X + "Name")?.Value == panelName);
    }

    private static string ViewsFolder() => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", "..",
        "src", "DriverUpdater.App", "Views"));
}
