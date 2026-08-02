using CommunityToolkit.Mvvm.ComponentModel;
using DriverUpdater.App.Ai;
using DriverUpdater.Core.Models;

namespace DriverUpdater.App.ViewModels;

/// <summary>
/// The confirmation card shown when the AI proposes changing app settings. Nothing is written
/// until the user presses Apply, and the card keeps its own state afterwards so the chat history
/// shows what was accepted and what was declined.
/// </summary>
public partial class ChatSettingProposalViewModel : ObservableObject
{
    public ChatSettingProposalViewModel(
        IReadOnlyList<ChatSettingChange> changes,
        AppLanguage language)
    {
        ArgumentNullException.ThrowIfNull(changes);
        Changes = changes;
        Language = language;
        Descriptions = changes.Select(change => change.Describe(language)).ToArray();
    }

    public IReadOnlyList<ChatSettingChange> Changes { get; }

    public AppLanguage Language { get; }

    public IReadOnlyList<string> Descriptions { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPending))]
    private bool _isResolved;

    [ObservableProperty]
    private string _resultText = string.Empty;

    public bool IsPending => !IsResolved;

    public bool IsHebrew => Language == AppLanguage.Hebrew;

    public string Title => IsHebrew
        ? "לשנות את ההגדרות האלה?"
        : "Change these settings?";

    public string ApplyLabel => IsHebrew ? "כן, שנה" : "Yes, change it";

    public string DeclineLabel => IsHebrew ? "לא עכשיו" : "Not now";

    /// <summary>
    /// The Settings window makes unattended installing its own opt-in, so a schedule that starts
    /// installing without anybody watching says so on the card too.
    /// </summary>
    public bool WarnsAboutUnattendedInstalls => Changes.Any(change =>
        string.Equals(change.Key, "schedule", StringComparison.Ordinal)
        && change.Value.StartsWith("update-", StringComparison.Ordinal));

    public string UnattendedInstallWarning => IsHebrew
        ? "שימו לב: הסריקה המתוזמנת תתקין מנהלי התקנים בלי לשאול, גם כשאתם לא מול המחשב."
        : "Note: the scheduled run will install drivers without asking, including while you are away.";

    public void MarkApplied(string? warning)
    {
        ResultText = string.IsNullOrWhiteSpace(warning)
            ? IsHebrew ? "ההגדרות עודכנו." : "Settings updated."
            : IsHebrew ? $"ההגדרות עודכנו. {warning}" : $"Settings updated. {warning}";
        IsResolved = true;
    }

    public void MarkFailed(string reason)
    {
        ResultText = IsHebrew
            ? $"לא הצלחתי לעדכן את ההגדרות: {reason}"
            : $"The settings could not be updated: {reason}";
        IsResolved = true;
    }

    public void MarkDeclined()
    {
        ResultText = IsHebrew ? "לא שיניתי כלום." : "Nothing was changed.";
        IsResolved = true;
    }
}
