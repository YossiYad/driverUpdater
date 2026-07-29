using DriverUpdater.Infrastructure.VendorInstallers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriverUpdater.Infrastructure.Tests.VendorInstallers;

public sealed class AmdInstallerContinuationWaiterTests
{
    [Fact]
    public async Task Waits_for_new_amd_continuation_process_and_a_quiet_period()
    {
        var snapshots = new Queue<IReadOnlySet<int>>(
        [
            new HashSet<int> { 10 },
            new HashSet<int> { 10, 20 },
            new HashSet<int> { 10, 20 },
            new HashSet<int> { 10 },
            new HashSet<int> { 10 }
        ]);
        var delayCalls = 0;
        var waiter = NewWaiter(snapshots, () => delayCalls++);
        const string installer = @"C:\Temp\whql-amd-software-adrenalin-edition-26.7.1.exe";

        var baseline = waiter.CaptureExistingProcesses(installer);
        await waiter.WaitForCompletionAsync(installer, baseline, CancellationToken.None);

        baseline.Should().BeEquivalentTo([10]);
        delayCalls.Should().Be(3);
        snapshots.Should().BeEmpty();
    }

    [Fact]
    public async Task Returns_after_discovery_window_when_amd_launcher_has_no_continuation()
    {
        var snapshots = new Queue<IReadOnlySet<int>>(
        [
            new HashSet<int>(),
            new HashSet<int>(),
            new HashSet<int>()
        ]);
        var delayCalls = 0;
        var waiter = NewWaiter(snapshots, () => delayCalls++);
        const string installer = @"C:\Temp\amd-software-adrenalin-edition.exe";

        var baseline = waiter.CaptureExistingProcesses(installer);
        await waiter.WaitForCompletionAsync(installer, baseline, CancellationToken.None);

        delayCalls.Should().Be(1);
        snapshots.Should().BeEmpty();
    }

    [Fact]
    public async Task Does_not_monitor_unrelated_vendor_installers()
    {
        var processReads = 0;
        var waiter = new AmdInstallerContinuationWaiter(
            NullLogger.Instance,
            () =>
            {
                processReads++;
                return new HashSet<int>();
            },
            (_, _) => Task.CompletedTask,
            TimeSpan.Zero,
            discoveryPolls: 2,
            quietPolls: 2,
            completionPolls: 5);
        const string installer = @"C:\Temp\nvidia-driver.exe";

        var baseline = waiter.CaptureExistingProcesses(installer);
        await waiter.WaitForCompletionAsync(installer, baseline, CancellationToken.None);

        processReads.Should().Be(0);
    }

    private static AmdInstallerContinuationWaiter NewWaiter(
        Queue<IReadOnlySet<int>> snapshots,
        Action onDelay) =>
        new(
            NullLogger.Instance,
            () => snapshots.Dequeue(),
            (_, _) =>
            {
                onDelay();
                return Task.CompletedTask;
            },
            TimeSpan.Zero,
            discoveryPolls: 2,
            quietPolls: 2,
            completionPolls: 5);
}
