using DriverUpdater.App.Ai;
using DriverUpdater.Core.Options;

namespace DriverUpdater.App.Services;

/// <summary>Outcome of applying a set of chat-proposed setting changes.</summary>
public sealed record ChatSettingsApplyResult(bool Succeeded, string? Warning = null)
{
    public static ChatSettingsApplyResult Success(string? warning = null) => new(true, warning);

    public static ChatSettingsApplyResult Failure(string reason) => new(false, reason);
}

/// <summary>
/// Reads and writes the settings the driver chat is allowed to change. Writing goes through the
/// same side effects as the Settings window, so a change made in conversation takes effect
/// immediately instead of only landing in settings.json.
/// </summary>
public interface IChatSettingsApplier
{
    Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default);

    Task<ChatSettingsApplyResult> ApplyAsync(
        IReadOnlyList<ChatSettingChange> changes,
        CancellationToken cancellationToken = default);
}
