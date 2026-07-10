namespace DriverUpdater.App.Logging;

/// <summary>
/// A single turn in the AI log conversation. <see cref="IsUser"/> distinguishes the
/// developer's questions from the assistant's answers so the view can style them and
/// the prompt builder can label them for the model.
/// </summary>
public sealed record LogChatMessage(bool IsUser, string Text)
{
    public string RoleLabel => IsUser ? "You" : "AI";
}
