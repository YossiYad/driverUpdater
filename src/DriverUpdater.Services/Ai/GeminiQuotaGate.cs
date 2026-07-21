using System.Globalization;
using System.Text.RegularExpressions;

namespace DriverUpdater.Services.Ai;

public sealed class GeminiQuotaGate
{
    private static readonly Regex RetryDelayPattern = new(
        "\\\"retryDelay\\\"\\s*:\\s*\\\"(?<seconds>[0-9]+(?:\\.[0-9]+)?)s\\\"",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly TimeZoneInfo PacificTimeZone = FindPacificTimeZone();

    private readonly object _sync = new();
    private readonly TimeProvider _clock;
    private readonly Dictionary<string, QuotaBlock> _blocks = new(StringComparer.Ordinal);

    private const string LegacyKey = "__legacy__";

    public GeminiQuotaGate()
        : this(TimeProvider.System)
    {
    }

    internal GeminiQuotaGate(TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
    }

    public event EventHandler<GeminiQuotaExceededEventArgs>? QuotaExceeded;

    public bool IsBlocked => IsKeyBlocked(LegacyKey);

    public bool IsKeyBlocked(string apiKey) => TryGetBlockedMessage(apiKey, out _);

    public bool AreAllBlocked(IReadOnlyList<string> apiKeys) =>
        apiKeys.Count > 0 && apiKeys.All(IsKeyBlocked);

    public bool TryGetBlockedMessage(out string message) =>
        TryGetBlockedMessage(LegacyKey, out message);

    public bool TryGetBlockedMessage(string apiKey, out string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        lock (_sync)
        {
            if (!_blocks.TryGetValue(apiKey, out var block))
            {
                message = string.Empty;
                return false;
            }

            if (block.BlockedUntilUtc <= _clock.GetUtcNow())
            {
                _blocks.Remove(apiKey);
                message = string.Empty;
                return false;
            }

            message = block.Message;
            return true;
        }
    }

    public void RecordTooManyRequests(HttpResponseMessage response, string responseBody) =>
        RecordTooManyRequests(LegacyKey, response, responseBody);

    public void RecordTooManyRequests(string apiKey, HttpResponseMessage response, string responseBody)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentNullException.ThrowIfNull(response);

        var now = _clock.GetUtcNow();
        var isDailyQuota = IsDailyQuota(responseBody);
        var blockedUntil = isDailyQuota
            ? GetNextDailyResetUtc(now)
            : GetTemporaryRetryTime(response, responseBody, now);
        var message = isDailyQuota
            ? $"Gemini's daily request quota is exhausted. It resets at midnight Pacific time, around {blockedUntil.ToLocalTime():g} on this computer."
            : $"Gemini is temporarily rate-limited. Try again after {blockedUntil.ToLocalTime():HH:mm:ss}.";
        GeminiQuotaExceededEventArgs? eventArgs = null;

        lock (_sync)
        {
            var wasBlocked = _blocks.TryGetValue(apiKey, out var previous)
                && previous.BlockedUntilUtc > now;
            var quotaChangedToDaily = isDailyQuota && previous is { IsDailyQuota: false };
            _blocks[apiKey] = new QuotaBlock(blockedUntil, message, isDailyQuota);

            if (!wasBlocked || quotaChangedToDaily)
            {
                eventArgs = new GeminiQuotaExceededEventArgs(isDailyQuota, blockedUntil);
            }
        }

        if (eventArgs is not null)
        {
            QuotaExceeded?.Invoke(this, eventArgs);
        }
    }

    private sealed record QuotaBlock(
        DateTimeOffset BlockedUntilUtc,
        string Message,
        bool IsDailyQuota);

    private static bool IsDailyQuota(string responseBody) =>
        responseBody.Contains("GenerateRequestsPerDay", StringComparison.OrdinalIgnoreCase)
        || responseBody.Contains("requests_per_day", StringComparison.OrdinalIgnoreCase)
        || responseBody.Contains("tokens_per_day", StringComparison.OrdinalIgnoreCase)
        || responseBody.Contains("PerDay", StringComparison.OrdinalIgnoreCase);

    private static DateTimeOffset GetTemporaryRetryTime(
        HttpResponseMessage response,
        string responseBody,
        DateTimeOffset now)
    {
        if (response.Headers.RetryAfter?.Date is { } retryDate && retryDate > now)
        {
            return retryDate;
        }

        var delay = response.Headers.RetryAfter?.Delta ?? TryReadRetryDelay(responseBody);
        if (delay is null || delay <= TimeSpan.Zero)
        {
            delay = TimeSpan.FromMinutes(1);
        }

        return now + delay.Value;
    }

    private static TimeSpan? TryReadRetryDelay(string responseBody)
    {
        var match = RetryDelayPattern.Match(responseBody);
        if (!match.Success
            || !double.TryParse(
                match.Groups["seconds"].Value,
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var seconds))
        {
            return null;
        }

        return TimeSpan.FromSeconds(seconds);
    }

    internal static DateTimeOffset GetNextDailyResetUtc(DateTimeOffset now)
    {
        var pacificNow = TimeZoneInfo.ConvertTime(now, PacificTimeZone);
        var nextPacificMidnight = DateTime.SpecifyKind(pacificNow.Date.AddDays(1), DateTimeKind.Unspecified);
        var pacificOffset = PacificTimeZone.GetUtcOffset(nextPacificMidnight);
        return new DateTimeOffset(nextPacificMidnight, pacificOffset).ToUniversalTime();
    }

    private static TimeZoneInfo FindPacificTimeZone()
    {
        foreach (var id in new[] { "Pacific Standard Time", "America/Los_Angeles" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
            }
        }

        return TimeZoneInfo.CreateCustomTimeZone(
            "Gemini Pacific Time",
            TimeSpan.FromHours(-8),
            "Gemini Pacific Time",
            "Gemini Pacific Time");
    }
}

public sealed class GeminiQuotaExceededEventArgs : EventArgs
{
    public GeminiQuotaExceededEventArgs(bool isDailyQuota, DateTimeOffset retryAtUtc)
    {
        IsDailyQuota = isDailyQuota;
        RetryAtUtc = retryAtUtc;
    }

    public bool IsDailyQuota { get; }

    public DateTimeOffset RetryAtUtc { get; }
}
