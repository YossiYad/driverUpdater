namespace DriverUpdater.Core.Abstractions;

public interface ISystemBootTimeProvider
{
    DateTimeOffset GetBootTimeUtc();
}
