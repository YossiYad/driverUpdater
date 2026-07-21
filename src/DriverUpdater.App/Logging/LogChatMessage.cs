using DriverUpdater.Core.Models;

namespace DriverUpdater.App.Logging;

/// <summary>
/// A single turn in the AI log conversation. <see cref="IsUser"/> distinguishes the
/// developer's questions from the assistant's answers so the view can style them and
/// the prompt builder can label them for the model.
/// </summary>
public sealed record LogChatMessage(
    bool IsUser,
    string Text,
    IReadOnlyList<string>? RecommendedHardwareIds = null,
    bool ShowScanAction = false,
    AppLanguage? ResponseLanguage = null)
{
    public string RoleLabel => IsUser ? "You" : "AI";

    public bool HasInstallAction => RecommendedHardwareIds is { Count: > 0 };

    public bool HasScanAction => ShowScanAction;

    public bool HasAction => HasInstallAction || HasScanAction;

    public int RecommendedCount => RecommendedHardwareIds?.Count ?? 0;

    public string InstallActionLabel => $"Install the {RecommendedCount} recommended update(s)";

    public string AskWhyActionLabel => "Ask me why";

    public string ScanActionLabel => "Scan now";
}
