using DriverUpdater.Core.Models;

namespace DriverUpdater.Core.Abstractions;

/// <summary>
/// General-purpose, free-form AI text completion (as opposed to the structured driver
/// verification protocol of <see cref="IAiVerifier"/>). Used for tasks like summarizing
/// the application logs. Returns null when AI is off / not configured or the call fails.
/// </summary>
public interface IAiTextCompleter
{
    AiProvider Provider { get; }

    bool IsConfigured { get; }

    Task<string?> CompleteAsync(string prompt, CancellationToken cancellationToken = default);
}
