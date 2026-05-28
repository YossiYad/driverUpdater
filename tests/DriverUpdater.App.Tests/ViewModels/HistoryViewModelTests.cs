using DriverUpdater.App.ViewModels;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using DriverUpdater.Core.Results;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriverUpdater.App.Tests.ViewModels;

public class HistoryViewModelTests
{
    [WpfFact]
    public async Task LoadAsync_populates_operations_from_repo()
    {
        var op1 = NewOperation(DateTimeOffset.UtcNow.AddMinutes(-10), UpdateStatus.Succeeded);
        var op2 = NewOperation(DateTimeOffset.UtcNow, UpdateStatus.Failed);
        var repo = new InMemoryHistoryRepo(new[] { op1, op2 });
        var vm = new HistoryViewModel(repo, new NoOpBackup(), NullLogger<HistoryViewModel>.Instance);

        await vm.LoadAsync();

        vm.Operations.Should().HaveCount(2);
        vm.IsLoading.Should().BeFalse();
        vm.StatusText.Should().Contain("2 operations");
    }

    [WpfFact]
    public async Task LoadAsync_shows_empty_message_when_no_operations()
    {
        var repo = new InMemoryHistoryRepo(Array.Empty<UpdateOperation>());
        var vm = new HistoryViewModel(repo, new NoOpBackup(), NullLogger<HistoryViewModel>.Instance);

        await vm.LoadAsync();

        vm.Operations.Should().BeEmpty();
        vm.StatusText.Should().Contain("No history");
    }

    [WpfFact]
    public async Task RollbackAsync_calls_backup_service_and_updates_status_to_rolled_back()
    {
        var op = NewOperation(DateTimeOffset.UtcNow, UpdateStatus.Succeeded, backupPath: @"C:\Backups\test");
        var repo = new InMemoryHistoryRepo(new[] { op });
        var backup = new RecordingBackup();
        var vm = new HistoryViewModel(repo, backup, NullLogger<HistoryViewModel>.Instance);
        await vm.LoadAsync();

        var row = vm.Operations[0];
        await vm.RollbackAsync(row);

        backup.RestoreInvocations.Should().Be(1);
        vm.Operations[0].Operation.Status.Should().Be(UpdateStatus.RolledBack);
        vm.StatusText.Should().Contain("Rolled back");
    }

    [WpfFact]
    public async Task RollbackAsync_records_error_message_when_backup_service_fails()
    {
        var op = NewOperation(DateTimeOffset.UtcNow, UpdateStatus.Succeeded, backupPath: @"C:\Backups\test");
        var repo = new InMemoryHistoryRepo(new[] { op });
        var backup = new RecordingBackup { FailureMessage = "Access denied" };
        var vm = new HistoryViewModel(repo, backup, NullLogger<HistoryViewModel>.Instance);
        await vm.LoadAsync();

        var row = vm.Operations[0];
        await vm.RollbackAsync(row);

        row.RollbackError.Should().Be("Access denied");
        vm.Operations[0].Operation.Status.Should().Be(UpdateStatus.Succeeded);
    }

    [WpfFact]
    public async Task RollbackAsync_is_noop_when_cannot_rollback()
    {
        var op = NewOperation(DateTimeOffset.UtcNow, UpdateStatus.Failed, backupPath: null);
        var repo = new InMemoryHistoryRepo(new[] { op });
        var backup = new RecordingBackup();
        var vm = new HistoryViewModel(repo, backup, NullLogger<HistoryViewModel>.Instance);
        await vm.LoadAsync();

        var row = vm.Operations[0];
        row.CanRollback.Should().BeFalse();
        await vm.RollbackAsync(row);

        backup.RestoreInvocations.Should().Be(0);
    }

    private static UpdateOperation NewOperation(DateTimeOffset startedAt, UpdateStatus status, string? backupPath = null)
    {
        var driver = new DriverInfo(
            DeviceId: "DEV\\X",
            HardwareId: "HW\\X",
            DeviceName: "Test Device",
            Category: DriverCategory.Network,
            Provider: "Test",
            Manufacturer: "Test",
            CurrentVersion: new Version(1, 0),
            CurrentDate: new DateOnly(2024, 1, 1),
            InfName: "oem1.inf",
            InfPath: null,
            IsSigned: true,
            DeviceClass: "Net");
        var candidate = new UpdateCandidate(
            ForHardwareId: "HW\\X",
            Source: UpdateSource.WindowsUpdate,
            NewVersion: new Version(2, 0),
            NewDate: new DateOnly(2026, 1, 1),
            DownloadUrl: new Uri("https://example.com/x.cab"),
            SizeBytes: 1024,
            KbArticle: null,
            IsSuperseded: false,
            SourceUpdateId: Guid.NewGuid().ToString(),
            SupersededIds: Array.Empty<string>());
        return UpdateOperation.NewPending(candidate, driver) with
        {
            StartedAt = startedAt,
            Status = status,
            BackupPath = backupPath
        };
    }

    private sealed class InMemoryHistoryRepo : IHistoryRepository
    {
        private readonly List<UpdateOperation> _operations;

        public InMemoryHistoryRepo(IEnumerable<UpdateOperation> initial)
        {
            _operations = initial.ToList();
        }

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task UpsertOperationAsync(UpdateOperation operation, CancellationToken cancellationToken = default)
        {
            var index = _operations.FindIndex(o => o.OperationId == operation.OperationId);
            if (index >= 0)
            {
                _operations[index] = operation;
            }
            else
            {
                _operations.Add(operation);
            }
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<UpdateOperation>> ListOperationsAsync(int limit = 200, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<UpdateOperation>>(_operations.OrderByDescending(o => o.StartedAt).ToArray());

        public Task<UpdateOperation?> GetOperationAsync(Guid operationId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_operations.FirstOrDefault(o => o.OperationId == operationId));
    }

    private sealed class NoOpBackup : IBackupService
    {
        public Task<Result<BackupArtifact>> BackupDriverAsync(DriverInfo driver, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result<bool>> RestoreFromBackupAsync(BackupArtifact artifact, CancellationToken cancellationToken = default) =>
            Task.FromResult<Result<bool>>(true);

        public IReadOnlyList<BackupArtifact> ListBackups() => Array.Empty<BackupArtifact>();
        public int PurgeBackupsOlderThan(TimeSpan age) => 0;
    }

    private sealed class RecordingBackup : IBackupService
    {
        public int RestoreInvocations { get; private set; }
        public string? FailureMessage { get; set; }

        public Task<Result<BackupArtifact>> BackupDriverAsync(DriverInfo driver, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result<bool>> RestoreFromBackupAsync(BackupArtifact artifact, CancellationToken cancellationToken = default)
        {
            RestoreInvocations++;
            if (!string.IsNullOrEmpty(FailureMessage))
            {
                return Task.FromResult(Result<bool>.Failure(ResultError.From("FAIL", FailureMessage)));
            }
            return Task.FromResult<Result<bool>>(true);
        }

        public IReadOnlyList<BackupArtifact> ListBackups() => Array.Empty<BackupArtifact>();
        public int PurgeBackupsOlderThan(TimeSpan age) => 0;
    }
}
