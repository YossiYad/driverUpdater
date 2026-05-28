using DriverUpdater.Core.Models;
using DriverUpdater.Core.Options;
using DriverUpdater.Infrastructure.History;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DriverUpdater.Infrastructure.Tests.History;

public class SqliteHistoryRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteHistoryRepository _repo;

    public SqliteHistoryRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "DriverUpdaterHistoryTests", Guid.NewGuid().ToString("N"), "history.db");
        var settings = new HistorySettings { DatabasePath = _dbPath };
        var monitor = new ConstantOptionsMonitor<HistorySettings>(settings);
        _repo = new SqliteHistoryRepository(monitor, NullLogger<SqliteHistoryRepository>.Instance);
    }

    public void Dispose()
    {
        try
        {
            var dir = Path.GetDirectoryName(_dbPath);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch
        {
        }
    }

    [Fact]
    public async Task InitializeAsync_creates_database_and_schema()
    {
        await _repo.InitializeAsync();

        File.Exists(_dbPath).Should().BeTrue();
        var operations = await _repo.ListOperationsAsync();
        operations.Should().BeEmpty();
    }

    [Fact]
    public async Task UpsertOperationAsync_inserts_and_then_updates_in_place()
    {
        await _repo.InitializeAsync();
        var op = NewOperation();

        await _repo.UpsertOperationAsync(op);
        var afterInsert = await _repo.ListOperationsAsync();
        afterInsert.Should().ContainSingle();
        afterInsert[0].Status.Should().Be(UpdateStatus.Pending);

        var advanced = op with
        {
            Status = UpdateStatus.Succeeded,
            CompletedAt = DateTimeOffset.UtcNow,
            BackupPath = @"C:\Backups\thing",
            RestorePointSequenceNumber = "42"
        };
        await _repo.UpsertOperationAsync(advanced);

        var afterUpdate = await _repo.ListOperationsAsync();
        afterUpdate.Should().ContainSingle();
        afterUpdate[0].Status.Should().Be(UpdateStatus.Succeeded);
        afterUpdate[0].BackupPath.Should().Be(@"C:\Backups\thing");
        afterUpdate[0].RestorePointSequenceNumber.Should().Be("42");
    }

    [Fact]
    public async Task GetOperationAsync_returns_null_when_missing()
    {
        await _repo.InitializeAsync();

        var missing = await _repo.GetOperationAsync(Guid.NewGuid());
        missing.Should().BeNull();
    }

    [Fact]
    public async Task GetOperationAsync_returns_record_when_present()
    {
        await _repo.InitializeAsync();
        var op = NewOperation();
        await _repo.UpsertOperationAsync(op);

        var fetched = await _repo.GetOperationAsync(op.OperationId);

        fetched.Should().NotBeNull();
        fetched!.OperationId.Should().Be(op.OperationId);
        fetched.TargetSnapshot.DeviceName.Should().Be(op.TargetSnapshot.DeviceName);
        fetched.Candidate.SourceUpdateId.Should().Be(op.Candidate.SourceUpdateId);
    }

    [Fact]
    public async Task ListOperationsAsync_orders_newest_first_and_respects_limit()
    {
        await _repo.InitializeAsync();
        var earlier = NewOperation(startedAt: new DateTimeOffset(2026, 5, 1, 10, 0, 0, TimeSpan.Zero));
        var later = NewOperation(startedAt: new DateTimeOffset(2026, 5, 27, 10, 0, 0, TimeSpan.Zero));
        await _repo.UpsertOperationAsync(earlier);
        await _repo.UpsertOperationAsync(later);

        var listed = await _repo.ListOperationsAsync(limit: 1);

        listed.Should().ContainSingle();
        listed[0].OperationId.Should().Be(later.OperationId);
    }

    [Fact]
    public async Task UpsertOperationAsync_roundtrips_complex_fields()
    {
        await _repo.InitializeAsync();
        var op = NewOperation(includeBackup: true);

        await _repo.UpsertOperationAsync(op);
        var fetched = await _repo.GetOperationAsync(op.OperationId);

        fetched.Should().NotBeNull();
        fetched!.Candidate.NewDate.Should().Be(op.Candidate.NewDate);
        fetched.TargetSnapshot.HardwareId.Should().Be(op.TargetSnapshot.HardwareId);
        fetched.Candidate.SupersededIds.Should().BeEquivalentTo(op.Candidate.SupersededIds);
    }

    private static UpdateOperation NewOperation(DateTimeOffset? startedAt = null, bool includeBackup = false)
    {
        var driver = new DriverInfo(
            DeviceId: "DEV\\X",
            HardwareId: "HW\\X",
            DeviceName: "Test Device",
            Category: DriverCategory.Network,
            Provider: "Test",
            Manufacturer: "Test",
            CurrentVersion: new Version(1, 0, 0, 0),
            CurrentDate: new DateOnly(2024, 1, 1),
            InfName: "oem1.inf",
            InfPath: null,
            IsSigned: true,
            DeviceClass: "Net");
        var candidate = new UpdateCandidate(
            ForHardwareId: "HW\\X",
            Source: UpdateSource.WindowsUpdate,
            NewVersion: new Version(2, 0, 0, 0),
            NewDate: new DateOnly(2026, 5, 1),
            DownloadUrl: new Uri("https://example.com/x.cab"),
            SizeBytes: 1024,
            KbArticle: "KB5000",
            IsSuperseded: false,
            SourceUpdateId: Guid.NewGuid().ToString(),
            SupersededIds: new[] { "old-1", "old-2" });
        var op = UpdateOperation.NewPending(candidate, driver);
        if (startedAt is not null)
        {
            op = op with { StartedAt = startedAt.Value };
        }
        if (includeBackup)
        {
            op = op with { BackupPath = @"C:\Backups\test" };
        }
        return op;
    }

    private sealed class ConstantOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public ConstantOptionsMonitor(T value) { CurrentValue = value; }
        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string> listener) => null;
    }
}
