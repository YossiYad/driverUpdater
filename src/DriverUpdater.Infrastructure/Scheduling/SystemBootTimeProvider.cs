using DriverUpdater.Core.Abstractions;

namespace DriverUpdater.Infrastructure.Scheduling;

public sealed class SystemBootTimeProvider : ISystemBootTimeProvider
{
    public DateTimeOffset GetBootTimeUtc() =>
        DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
}
