using System.Globalization;
using DriverUpdater.Core.Models;
using DriverUpdater.Core.Options;

namespace DriverUpdater.App.Ai;

/// <summary>
/// One app setting the driver chat is allowed to change, with everything the prompt, the
/// confirmation card, and the applier need: how to read it, how to write it, and how to
/// describe the new value to the user in both supported languages.
/// </summary>
public sealed record ChatSettingDefinition(
    string Key,
    string Purpose,
    IReadOnlyList<string> AllowedValues,
    Func<AppSettings, string> Read,
    Func<AppSettings, string, bool> TryWrite,
    Func<string, string> DescribeEnglish,
    Func<string, string> DescribeHebrew)
{
    public string AllowedValuesText => string.Join(" | ", AllowedValues);
}

/// <summary>A resolved, validated setting change waiting for the user to confirm it.</summary>
public sealed record ChatSettingChange(ChatSettingDefinition Definition, string Value)
{
    public string Key => Definition.Key;

    public string Describe(AppLanguage language) => language == AppLanguage.Hebrew
        ? Definition.DescribeHebrew(Value)
        : Definition.DescribeEnglish(Value);
}

/// <summary>
/// The whitelist of settings the AI may propose changing. Anything not listed here is
/// ignored when it comes back from the model, so a hallucinated key can never write to
/// settings.json.
/// </summary>
public static class ChatSettingCatalog
{
    private static readonly string[] OnOff = ["on", "off"];

    private static readonly IReadOnlyDictionary<string, DayOfWeek> DayNames =
        new Dictionary<string, DayOfWeek>(StringComparer.OrdinalIgnoreCase)
        {
            ["sunday"] = DayOfWeek.Sunday,
            ["monday"] = DayOfWeek.Monday,
            ["tuesday"] = DayOfWeek.Tuesday,
            ["wednesday"] = DayOfWeek.Wednesday,
            ["thursday"] = DayOfWeek.Thursday,
            ["friday"] = DayOfWeek.Friday,
            ["saturday"] = DayOfWeek.Saturday
        };

    private static readonly string[] HebrewDayNames =
        ["ראשון", "שני", "שלישי", "רביעי", "חמישי", "שישי", "שבת"];

    public static IReadOnlyList<ChatSettingDefinition> All { get; } = Build();

    private static readonly IReadOnlyDictionary<string, ChatSettingDefinition> ByKey =
        All.ToDictionary(definition => definition.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves a key/value pair produced by the model. Returns false for an unknown key or a
    /// value outside the allowed set, which is how invented options get dropped.
    /// </summary>
    public static bool TryResolve(string? key, string? value, out ChatSettingChange change)
    {
        change = null!;
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (!ByKey.TryGetValue(key.Trim(), out var definition))
        {
            return false;
        }

        var normalized = Normalize(definition, value.Trim());
        if (normalized is null)
        {
            return false;
        }

        // A definition validates by writing into a throwaway copy: if the write is rejected the
        // value is not something we can apply, whatever it looked like.
        if (!definition.TryWrite(new AppSettings(), normalized))
        {
            return false;
        }

        change = new ChatSettingChange(definition, normalized);
        return true;
    }

    /// <summary>The current value of every controllable setting, for the prompt.</summary>
    public static IReadOnlyList<string> DescribeCurrent(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return All.Select(definition => $"{definition.Key} = {definition.Read(settings)}").ToArray();
    }

    /// <summary>The key catalogue the prompt shows the model, one line per setting.</summary>
    public static IReadOnlyList<string> DescribeOptions() =>
        All.Select(definition =>
            $"{definition.Key}: {definition.AllowedValuesText} - {definition.Purpose}").ToArray();

    private static string? Normalize(ChatSettingDefinition definition, string value)
    {
        // Free-form values (a time, a day count) are validated by the writer instead of a list.
        if (definition.AllowedValues.Count == 1 && definition.AllowedValues[0].StartsWith('<'))
        {
            return value;
        }

        return definition.AllowedValues.FirstOrDefault(
            allowed => string.Equals(allowed, value, StringComparison.OrdinalIgnoreCase));
    }

    private static ChatSettingDefinition[] Build() =>
    [
        new(
            "schedule",
            "what the scheduled run does",
            ["off", "scan-only", "update-all", "update-selected", "update-ai"],
            settings => settings.Schedule.Mode switch
            {
                ScheduleMode.Manual => "off",
                ScheduleMode.ScanOnly => "scan-only",
                _ => settings.Schedule.AutoUpdateScope switch
                {
                    AutoUpdateScope.SelectedDrivers => "update-selected",
                    AutoUpdateScope.AiRecommended => "update-ai",
                    _ => "update-all"
                }
            },
            (settings, value) =>
            {
                switch (value.ToLowerInvariant())
                {
                    case "off":
                        settings.Schedule.Mode = ScheduleMode.Manual;
                        return true;
                    case "scan-only":
                        settings.Schedule.Mode = ScheduleMode.ScanOnly;
                        return true;
                    case "update-all":
                        settings.Schedule.Mode = ScheduleMode.ScanAndUpdate;
                        settings.Schedule.AutoUpdateScope = AutoUpdateScope.AllDrivers;
                        return true;
                    case "update-selected":
                        settings.Schedule.Mode = ScheduleMode.ScanAndUpdate;
                        settings.Schedule.AutoUpdateScope = AutoUpdateScope.SelectedDrivers;
                        return true;
                    case "update-ai":
                        settings.Schedule.Mode = ScheduleMode.ScanAndUpdate;
                        settings.Schedule.AutoUpdateScope = AutoUpdateScope.AiRecommended;
                        return true;
                    default:
                        return false;
                }
            },
            value => value switch
            {
                "off" => "Turn the scheduled driver run off.",
                "scan-only" => "Schedule a scan only, with no automatic installing.",
                "update-all" => "Let the scheduled run install every update it finds.",
                "update-selected" => "Let the scheduled run install only the drivers you picked.",
                _ => "Let the AI decide what the scheduled run installs."
            },
            value => value switch
            {
                "off" => "לכבות את הסריקה המתוזמנת.",
                "scan-only" => "לתזמן סריקה בלבד, בלי התקנה אוטומטית.",
                "update-all" => "לאפשר לסריקה המתוזמנת להתקין כל עדכון שהיא מוצאת.",
                "update-selected" => "לאפשר לסריקה המתוזמנת להתקין רק את מנהלי ההתקנים שבחרת.",
                _ => "לתת ל-AI להחליט מה הסריקה המתוזמנת מתקינה."
            }),

        new(
            "schedule.cadence",
            "how often the scheduled run happens",
            ["daily", "weekly", "monthly"],
            settings => settings.Schedule.Cadence.ToString().ToLowerInvariant(),
            (settings, value) =>
            {
                if (!Enum.TryParse<ScheduleCadence>(value, ignoreCase: true, out var cadence))
                {
                    return false;
                }
                settings.Schedule.Cadence = cadence;
                return true;
            },
            value => $"Run the schedule {value}.",
            value => value switch
            {
                "daily" => "להריץ את התזמון כל יום.",
                "weekly" => "להריץ את התזמון כל שבוע.",
                _ => "להריץ את התזמון כל חודש."
            }),

        new(
            "schedule.time",
            "the time of day the scheduled run starts, 24-hour HH:mm",
            ["<HH:mm>"],
            settings => settings.Schedule.TimeOfDay.ToString("HH\\:mm", CultureInfo.InvariantCulture),
            (settings, value) =>
            {
                if (!TimeOnly.TryParseExact(value, "H\\:mm", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var time)
                    && !TimeOnly.TryParseExact(value, "HH\\:mm", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out time))
                {
                    return false;
                }
                settings.Schedule.TimeOfDay = time;
                return true;
            },
            value => $"Start the scheduled run at {value}.",
            value => $"להתחיל את הסריקה המתוזמנת בשעה {value}."),

        new(
            "schedule.day",
            "which weekday a weekly schedule runs on",
            ["sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday"],
            settings => settings.Schedule.DayOfWeek.ToString().ToLowerInvariant(),
            (settings, value) =>
            {
                if (!DayNames.TryGetValue(value, out var day))
                {
                    return false;
                }
                settings.Schedule.DayOfWeek = day;
                return true;
            },
            value => $"Run the weekly schedule on {value}.",
            value => DayNames.TryGetValue(value, out var day)
                ? $"להריץ את התזמון השבועי ביום {HebrewDayNames[(int)day]}."
                : "לשנות את יום התזמון השבועי."),

        new(
            "schedule.ai-risk",
            "how much risk an AI-decided scheduled run may accept",
            ["safe-only", "safe-and-caution"],
            settings => settings.Schedule.AiRiskTolerance == AiAutoUpdateRiskTolerance.SafeAndCaution
                ? "safe-and-caution"
                : "safe-only",
            (settings, value) =>
            {
                settings.Schedule.AiRiskTolerance = value.Equals("safe-and-caution", StringComparison.OrdinalIgnoreCase)
                    ? AiAutoUpdateRiskTolerance.SafeAndCaution
                    : AiAutoUpdateRiskTolerance.SafeOnly;
                return true;
            },
            value => value == "safe-and-caution"
                ? "Let the AI schedule install safe updates and the ones flagged for caution."
                : "Let the AI schedule install safe updates only.",
            value => value == "safe-and-caution"
                ? "לאפשר לתזמון ה-AI להתקין עדכונים בטוחים וגם כאלה שסומנו לזהירות."
                : "לאפשר לתזמון ה-AI להתקין עדכונים בטוחים בלבד."),

        new(
            "close-button",
            "what the window X button does",
            ["exit", "background"],
            settings => settings.Application.CloseBehavior == WindowCloseBehavior.KeepRunningInBackground
                ? "background"
                : "exit",
            (settings, value) =>
            {
                settings.Application.CloseBehavior = value.Equals("background", StringComparison.OrdinalIgnoreCase)
                    ? WindowCloseBehavior.KeepRunningInBackground
                    : WindowCloseBehavior.ExitApplication;
                return true;
            },
            value => value == "background"
                ? "Make the X button leave DriverUpdater running in the notification area."
                : "Make the X button close DriverUpdater completely.",
            value => value == "background"
                ? "שכפתור ה-X ישאיר את DriverUpdater פעילה באזור ההתראות."
                : "שכפתור ה-X יסגור את DriverUpdater לגמרי."),

        new(
            "start-with-windows",
            "whether DriverUpdater starts with Windows",
            OnOff,
            settings => settings.Application.StartWithWindows ? "on" : "off",
            (settings, value) =>
            {
                settings.Application.StartWithWindows = IsOn(value);
                return true;
            },
            value => IsOn(value)
                ? "Start DriverUpdater automatically with Windows."
                : "Stop starting DriverUpdater with Windows.",
            value => IsOn(value)
                ? "להפעיל את DriverUpdater אוטומטית עם Windows."
                : "להפסיק להפעיל את DriverUpdater עם Windows."),

        new(
            "start-minimized",
            "whether the Windows startup launch stays hidden in the notification area",
            OnOff,
            settings => settings.Application.StartMinimized ? "on" : "off",
            (settings, value) =>
            {
                settings.Application.StartMinimized = IsOn(value);
                return true;
            },
            value => IsOn(value)
                ? "Start hidden in the notification area instead of opening the window."
                : "Open the window when DriverUpdater starts with Windows.",
            value => IsOn(value)
                ? "להתחיל מוסתר באזור ההתראות במקום לפתוח את החלון."
                : "לפתוח את החלון כש-DriverUpdater עולה עם Windows."),

        new(
            "ai.language",
            "the language the AI answers in",
            ["english", "hebrew"],
            settings => settings.Ai.ResponseLanguage == AppLanguage.Hebrew ? "hebrew" : "english",
            (settings, value) =>
            {
                settings.Ai.ResponseLanguage = value.Equals("hebrew", StringComparison.OrdinalIgnoreCase)
                    ? AppLanguage.Hebrew
                    : AppLanguage.English;
                return true;
            },
            value => value == "hebrew" ? "Answer in Hebrew from now on." : "Answer in English from now on.",
            value => value == "hebrew" ? "לענות מעכשיו בעברית." : "לענות מעכשיו באנגלית."),

        new(
            "ai.web-search",
            "whether the AI may search the web while checking drivers",
            OnOff,
            settings => settings.Ai.EnableWebSearch ? "on" : "off",
            (settings, value) =>
            {
                settings.Ai.EnableWebSearch = IsOn(value);
                return true;
            },
            value => IsOn(value)
                ? "Let the AI search the web while checking drivers."
                : "Stop the AI from searching the web while checking drivers.",
            value => IsOn(value)
                ? "לאפשר ל-AI לחפש באינטרנט בזמן בדיקת מנהלי ההתקנים."
                : "למנוע מה-AI לחפש באינטרנט בזמן בדיקת מנהלי ההתקנים."),

        new(
            "app-updates.check-on-startup",
            "whether DriverUpdater checks for its own new version on startup",
            OnOff,
            settings => settings.Updater.CheckOnStartup ? "on" : "off",
            (settings, value) =>
            {
                settings.Updater.CheckOnStartup = IsOn(value);
                return true;
            },
            value => IsOn(value)
                ? "Check for a new DriverUpdater version on startup."
                : "Stop checking for a new DriverUpdater version on startup.",
            value => IsOn(value)
                ? "לבדוק בהפעלה אם יש גרסה חדשה של DriverUpdater."
                : "להפסיק לבדוק בהפעלה אם יש גרסה חדשה של DriverUpdater."),

        new(
            "app-updates.auto-install",
            "whether a found DriverUpdater update installs without asking",
            OnOff,
            settings => settings.Updater.AutoApply ? "on" : "off",
            (settings, value) =>
            {
                settings.Updater.AutoApply = IsOn(value);
                return true;
            },
            value => IsOn(value)
                ? "Install a new DriverUpdater version without asking."
                : "Ask before installing a new DriverUpdater version.",
            value => IsOn(value)
                ? "להתקין גרסה חדשה של DriverUpdater בלי לשאול."
                : "לשאול לפני התקנת גרסה חדשה של DriverUpdater."),

        new(
            "sources.windows-update",
            "whether Windows Update is used as a driver source",
            OnOff,
            settings => settings.Updater.WindowsUpdateEnabled ? "on" : "off",
            (settings, value) =>
            {
                settings.Updater.WindowsUpdateEnabled = IsOn(value);
                return true;
            },
            value => IsOn(value) ? "Use Windows Update as a driver source." : "Stop using Windows Update as a driver source.",
            value => IsOn(value)
                ? "להשתמש ב-Windows Update כמקור למנהלי התקנים."
                : "להפסיק להשתמש ב-Windows Update כמקור למנהלי התקנים."),

        new(
            "sources.microsoft-catalog",
            "whether the Microsoft Update Catalog is used as a driver source",
            OnOff,
            settings => settings.Catalog.Enabled ? "on" : "off",
            (settings, value) =>
            {
                settings.Catalog.Enabled = IsOn(value);
                return true;
            },
            value => IsOn(value)
                ? "Use the Microsoft Update Catalog as a driver source."
                : "Stop using the Microsoft Update Catalog as a driver source.",
            value => IsOn(value)
                ? "להשתמש בקטלוג העדכונים של Microsoft כמקור למנהלי התקנים."
                : "להפסיק להשתמש בקטלוג העדכונים של Microsoft כמקור למנהלי התקנים."),

        new(
            "sources.oem",
            "whether OEM and vendor pages are used as a driver source",
            OnOff,
            settings => settings.Updater.OemSourcesEnabled ? "on" : "off",
            (settings, value) =>
            {
                settings.Updater.OemSourcesEnabled = IsOn(value);
                return true;
            },
            value => IsOn(value)
                ? "Use OEM and vendor pages as a driver source."
                : "Stop using OEM and vendor pages as a driver source.",
            value => IsOn(value)
                ? "להשתמש בדפי היצרן וה-OEM כמקור למנהלי התקנים."
                : "להפסיק להשתמש בדפי היצרן וה-OEM כמקור למנהלי התקנים."),

        new(
            "logs.auto-cleanup",
            "whether old log files are deleted automatically",
            OnOff,
            settings => settings.LogCleanup.Enabled ? "on" : "off",
            (settings, value) =>
            {
                settings.LogCleanup.Enabled = IsOn(value);
                return true;
            },
            value => IsOn(value) ? "Delete old log files automatically." : "Keep every log file.",
            value => IsOn(value) ? "למחוק אוטומטית קובצי יומן ישנים." : "לשמור את כל קובצי היומן."),

        new(
            "logs.retention-days",
            $"how many days of logs to keep, {LogCleanupSettings.MinimumRetentionDays}-{LogCleanupSettings.MaximumRetentionDays}",
            ["<days>"],
            settings => settings.LogCleanup.RetentionDays.ToString(CultureInfo.InvariantCulture),
            (settings, value) =>
            {
                if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var days)
                    || days < LogCleanupSettings.MinimumRetentionDays
                    || days > LogCleanupSettings.MaximumRetentionDays)
                {
                    return false;
                }
                settings.LogCleanup.RetentionDays = days;
                return true;
            },
            value => $"Keep {value} days of log files.",
            value => $"לשמור קובצי יומן של {value} ימים."),

        new(
            "guide-on-startup",
            "whether the welcome guide opens on startup",
            OnOff,
            settings => settings.Onboarding.ShowOnStartup ? "on" : "off",
            (settings, value) =>
            {
                settings.Onboarding.ShowOnStartup = IsOn(value);
                return true;
            },
            value => IsOn(value) ? "Open the welcome guide on startup." : "Stop opening the welcome guide on startup.",
            value => IsOn(value) ? "לפתוח את מדריך הפתיחה בכל הפעלה." : "להפסיק לפתוח את מדריך הפתיחה בהפעלה.")
    ];

    private static bool IsOn(string value) =>
        value.Equals("on", StringComparison.OrdinalIgnoreCase);
}
