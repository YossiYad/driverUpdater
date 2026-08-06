using DriverUpdater.Infrastructure.Concurrency;
using FluentAssertions;

namespace DriverUpdater.Infrastructure.Tests.Concurrency;

public class FileInstallExecutionGateTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "DriverUpdaterInstallGateTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task AcquireAsync_waits_until_another_process_lease_is_released()
    {
        var path = Path.Combine(_directory, "install.lock");
        var firstGate = new FileInstallExecutionGate(path);
        var secondGate = new FileInstallExecutionGate(path);
        var firstLease = await firstGate.AcquireAsync();

        try
        {
            var secondAcquire = secondGate.AcquireAsync().AsTask();
            secondAcquire.IsCompleted.Should().BeFalse();

            await firstLease.DisposeAsync();
            await using var secondLease = await secondAcquire.WaitAsync(TimeSpan.FromSeconds(2));
            secondAcquire.IsCompletedSuccessfully.Should().BeTrue();
        }
        finally
        {
            await firstLease.DisposeAsync();
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
