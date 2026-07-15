using System.Net;

namespace DriverUpdater.Services.Ai;

public sealed class GeminiQuotaGate
{
    private readonly object _sync = new();
    private DateTimeOffset _blockedUntilUtc;
    private string? _blockedMessage;

    public bool TryGetBlockedMessage(out string message)
    {
        lock (_sync)
        {
            if (_blockedUntilUtc <= DateTimeOffset.UtcNow || string.IsNullOrWhiteSpace(_blockedMessage))
            {
                _blockedUntilUtc = default;
                _blockedMessage = null;
                message = string.Empty;
                return false;
            }

            message = _blockedMessage;
            return true;
        }
    }

    public void RecordTooManyRequests(HttpResponseMessage response, string responseBody)
    {
        ArgumentNullException.ThrowIfNull(response);

        var now = DateTimeOffset.UtcNow;
        var isDailyQuota = responseBody.Contains(
            "GenerateRequestsPerDay",
            StringComparison.OrdinalIgnoreCase);
        var blockedUntil = isDailyQuota
            ? new DateTimeOffset(now.UtcDateTime.Date.AddDays(1), TimeSpan.Zero)
            : now + (response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(1));
        var message = isDailyQuota
            ? "Gemini's daily request quota is exhausted. Try again after the quota resets, switch to Ollama, or configure additional Gemini quota."
            : $"Gemini is temporarily rate-limited. Try again after {blockedUntil.ToLocalTime():HH:mm:ss}.";

        lock (_sync)
        {
            _blockedUntilUtc = blockedUntil;
            _blockedMessage = message;
        }
    }
}
