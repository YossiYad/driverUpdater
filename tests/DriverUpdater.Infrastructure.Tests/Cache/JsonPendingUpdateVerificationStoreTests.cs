using DriverUpdater.Core.Models;
using DriverUpdater.Infrastructure.Cache;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriverUpdater.Infrastructure.Tests.Cache;

public class JsonPendingUpdateVerificationStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(),
        "DriverUpdaterPendingVerificationTests",
        Guid.NewGuid().ToString("N"),
        "pending.json");

    [Fact]
    public async Task Save_load_and_clear_round_trip_pending_batch()
    {
        var store = new JsonPendingUpdateVerificationStore(
            NullLogger<JsonPendingUpdateVerificationStore>.Instance,
            _path);
        var operation = NewOperation();
        var batch = new PendingUpdateVerificationBatch(
            Guid.NewGuid(),
            new DateTimeOffset(2026, 7, 14, 12, 0, 0, TimeSpan.Zero),
            new[] { operation });

        await store.SaveAsync(batch);
        var loaded = await store.LoadAsync();

        loaded.Should().NotBeNull();
        loaded!.BatchId.Should().Be(batch.BatchId);
        loaded.Operations.Should().ContainSingle();
        loaded.Operations[0].OperationId.Should().Be(operation.OperationId);
        loaded.Operations[0].Candidate.NewVersion.Should().Be(new Version(2, 0, 0, 0));

        await store.ClearAsync();
        (await store.LoadAsync()).Should().BeNull();
    }

    public void Dispose()
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch
        {
        }
    }

    private static UpdateOperation NewOperation()
    {
        var driver = DriverInfo.Empty("DEVICE\\1") with
        {
            DeviceId = "DEVICE\\1",
            HardwareId = "HARDWARE\\1",
            DeviceName = "Test device",
            CurrentVersion = new Version(1, 0, 0, 0)
        };
        var candidate = new UpdateCandidate(
            driver.HardwareId,
            UpdateSource.WindowsUpdate,
            new Version(2, 0, 0, 0),
            new DateOnly(2026, 1, 1),
            new Uri("about:blank"),
            0,
            null,
            false,
            "update-1",
            Array.Empty<string>());
        return UpdateOperation.NewPending(candidate, driver) with
        {
            Status = UpdateStatus.Succeeded,
            ErrorMessage = "Reboot required to complete installation.",
            CompletedAt = DateTimeOffset.UtcNow
        };
    }
}
