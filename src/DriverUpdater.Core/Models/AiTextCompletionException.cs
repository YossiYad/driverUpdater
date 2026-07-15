namespace DriverUpdater.Core.Models;

public enum AiTextCompletionFailureReason
{
    ProviderUnavailable = 0,
    QuotaExceeded
}

public sealed class AiTextCompletionException : Exception
{
    public AiTextCompletionException(
        AiTextCompletionFailureReason reason,
        string message)
        : base(message)
    {
        Reason = reason;
    }

    public AiTextCompletionFailureReason Reason { get; }
}
