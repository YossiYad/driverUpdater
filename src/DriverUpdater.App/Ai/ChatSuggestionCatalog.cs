using DriverUpdater.Core.Models;

namespace DriverUpdater.App.Ai;

/// <summary>One conversation starter offered as a chip above the chat input.</summary>
public sealed record ChatSuggestion(string Text);

/// <summary>
/// The pool the drifting suggestion chips are drawn from. Half the entries are about the
/// scanned drivers, half nudge the user towards settings the chat can change for them, so the
/// feature discovers itself without a tutorial.
/// </summary>
public static class ChatSuggestionCatalog
{
    /// <summary>How many chips are on screen at once.</summary>
    public const int VisibleCount = 3;

    private static readonly ChatSuggestion[] English =
    [
        new("What should I update first?"),
        new("Is it safe to update my graphics driver?"),
        new("Which of these updates can wait?"),
        new("Explain what this scan found, in simple words."),
        new("Are any of my drivers really out of date?"),
        new("Which driver is most likely causing problems?"),
        new("Should I use the vendor driver or the Windows one?"),
        new("What would you leave alone on this PC?"),
        new("Are any of these updates risky for my hardware?"),
        new("Do I need to restart after these updates?"),
        new("What does this network driver actually do?"),
        new("Find the newest stable driver for my chipset."),
        new("Turn on a weekly scan for me."),
        new("Schedule automatic driver updates every week."),
        new("Set the scheduled run to Sunday at 22:00."),
        new("Let the AI decide what gets installed automatically."),
        new("Turn the scheduled updates off."),
        new("Only scan on a schedule, do not install anything."),
        new("Make the X button close the app completely."),
        new("Keep the app running in the tray when I press X."),
        new("Start DriverUpdater with Windows, hidden."),
        new("Stop starting DriverUpdater with Windows."),
        new("Answer me in Hebrew from now on."),
        new("Turn off web search when checking drivers."),
        new("Check for app updates on startup automatically."),
        new("Install app updates without asking me."),
        new("Stop using the Microsoft Update Catalog."),
        new("Turn off vendor and OEM driver sources."),
        new("Keep only 14 days of log files."),
        new("Stop opening the welcome guide every time."),
        new("Which settings can you change for me?"),
        new("What is my schedule set to right now?"),
        new("Set up the safest automatic update option."),
        new("Only let the AI install safe updates."),
        new("Give me the most cautious setup you can.")
    ];

    private static readonly ChatSuggestion[] Hebrew =
    [
        new("מה כדאי לי לעדכן קודם?"),
        new("בטוח לעדכן את הכרטיס הגרפי?"),
        new("אילו עדכונים אפשר לדחות?"),
        new("תסביר לי במילים פשוטות מה נמצא בסריקה."),
        new("יש לי מנהלי התקנים ממש ישנים?"),
        new("איזה מנהל התקן הכי עלול לגרום לבעיות?"),
        new("עדיף מנהל התקן של היצרן או של Windows?"),
        new("על מה לא הייתי נוגע במחשב הזה?"),
        new("יש עדכונים מסוכנים לחומרה שלי?"),
        new("אצטרך להפעיל מחדש אחרי העדכונים?"),
        new("מה מנהל ההתקן של הרשת בכלל עושה?"),
        new("תמצא את הגרסה היציבה החדשה ביותר לשבב שלי."),
        new("תפעיל לי סריקה שבועית."),
        new("תתזמן עדכון מנהלי התקנים אוטומטי כל שבוע."),
        new("תקבע את הסריקה המתוזמנת ליום ראשון בשעה 22:00."),
        new("תן ל-AI להחליט מה יותקן אוטומטית."),
        new("תכבה את העדכונים המתוזמנים."),
        new("רק לסרוק בתזמון, בלי להתקין כלום."),
        new("שכפתור ה-X יסגור את האפליקציה לגמרי."),
        new("שהאפליקציה תישאר באזור ההתראות כשאני לוחץ X."),
        new("תפעיל את DriverUpdater עם Windows, מוסתר."),
        new("תפסיק להפעיל את DriverUpdater עם Windows."),
        new("תענה לי מעכשיו באנגלית."),
        new("תכבה חיפוש באינטרנט בבדיקת מנהלי התקנים."),
        new("תבדוק אוטומטית עדכוני אפליקציה בהפעלה."),
        new("תתקין עדכוני אפליקציה בלי לשאול אותי."),
        new("תפסיק להשתמש בקטלוג העדכונים של Microsoft."),
        new("תכבה את מקורות היצרן וה-OEM."),
        new("תשמור רק 14 ימים של קובצי יומן."),
        new("תפסיק לפתוח את מדריך הפתיחה בכל הפעלה."),
        new("אילו הגדרות אתה יכול לשנות בשבילי?"),
        new("מה מוגדר לי כרגע בתזמון?"),
        new("תגדיר לי את אפשרות העדכון האוטומטי הבטוחה ביותר."),
        new("תאפשר ל-AI להתקין רק עדכונים בטוחים."),
        new("תן לי את ההגדרה הכי זהירה שאפשר.")
    ];

    public static IReadOnlyList<ChatSuggestion> For(AppLanguage language) =>
        language == AppLanguage.Hebrew ? Hebrew : English;
}
