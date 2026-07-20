using System.Net;
using DriverUpdater.Services.Ai;
using FluentAssertions;

namespace DriverUpdater.Services.Tests.Ai;

public class GeminiQuotaGateTests
{
    [Fact]
    public void Daily_quota_uses_next_Pacific_midnight_and_notifies_once()
    {
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero));
        var gate = new GeminiQuotaGate(clock);
        var notices = new List<GeminiQuotaExceededEventArgs>();
        gate.QuotaExceeded += (_, notice) => notices.Add(notice);
        using var firstResponse = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        using var secondResponse = new HttpResponseMessage(HttpStatusCode.TooManyRequests);

        gate.RecordTooManyRequests(firstResponse, "Quota exceeded: GenerateRequestsPerDay");
        gate.RecordTooManyRequests(secondResponse, "Quota exceeded: GenerateRequestsPerDay");

        notices.Should().ContainSingle();
        notices[0].IsDailyQuota.Should().BeTrue();
        notices[0].RetryAtUtc.Should().Be(new DateTimeOffset(2026, 7, 16, 7, 0, 0, TimeSpan.Zero));
        gate.TryGetBlockedMessage(out var message).Should().BeTrue();
        message.Should().Contain("midnight Pacific time");
    }

    [Fact]
    public void Temporary_quota_uses_retry_delay_from_Gemini_response()
    {
        var now = new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);
        var gate = new GeminiQuotaGate(new FixedTimeProvider(now));
        GeminiQuotaExceededEventArgs? notice = null;
        gate.QuotaExceeded += (_, value) => notice = value;
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);

        gate.RecordTooManyRequests(response, "{\"details\":[{\"retryDelay\":\"50s\"}]}");

        notice.Should().NotBeNull();
        notice!.IsDailyQuota.Should().BeFalse();
        notice.RetryAtUtc.Should().Be(now.AddSeconds(50));
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
