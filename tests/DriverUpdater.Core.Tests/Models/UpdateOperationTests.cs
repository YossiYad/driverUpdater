using DriverUpdater.Core.Models;
using FluentAssertions;

namespace DriverUpdater.Core.Tests.Models;

public class UpdateOperationTests
{
    [Theory]
    [InlineData(UpdateStatus.Succeeded, true)]
    [InlineData(UpdateStatus.Failed, true)]
    [InlineData(UpdateStatus.RolledBack, true)]
    [InlineData(UpdateStatus.Skipped, true)]
    [InlineData(UpdateStatus.Cancelled, true)]
    [InlineData(UpdateStatus.Pending, false)]
    [InlineData(UpdateStatus.CreatingRestorePoint, false)]
    [InlineData(UpdateStatus.BackingUp, false)]
    [InlineData(UpdateStatus.Downloading, false)]
    [InlineData(UpdateStatus.Installing, false)]
    public void IsTerminal_reflects_status(UpdateStatus status, bool expected)
    {
        var op = NewOperation(status, completedAt: null);

        op.IsTerminal.Should().Be(expected);
    }

    [Fact]
    public void Duration_is_null_until_completed_at_is_set()
    {
        var op = NewOperation(UpdateStatus.Installing, completedAt: null);

        op.Duration.Should().BeNull();
    }

    [Fact]
    public void Duration_is_completed_minus_started_when_both_set()
    {
        var started = new DateTimeOffset(2026, 5, 27, 10, 0, 0, TimeSpan.Zero);
        var completed = started.AddMinutes(3);
        var op = NewOperation(UpdateStatus.Succeeded, completedAt: completed, startedAt: started);

        op.Duration.Should().Be(TimeSpan.FromMinutes(3));
    }

    [Fact]
    public void NewPending_creates_operation_with_pending_status_and_fresh_guid()
    {
        var candidate = NewCandidate();
        var target = NewDriver();

        var op = UpdateOperation.NewPending(candidate, target);

        op.Status.Should().Be(UpdateStatus.Pending);
        op.OperationId.Should().NotBe(Guid.Empty);
        op.Candidate.Should().Be(candidate);
        op.TargetSnapshot.Should().Be(target);
        op.ErrorMessage.Should().BeNull();
        op.BackupPath.Should().BeNull();
        op.RestorePointSequenceNumber.Should().BeNull();
        op.CompletedAt.Should().BeNull();
        op.StartedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void NewPending_creates_unique_operation_ids()
    {
        var candidate = NewCandidate();
        var target = NewDriver();

        var op1 = UpdateOperation.NewPending(candidate, target);
        var op2 = UpdateOperation.NewPending(candidate, target);

        op1.OperationId.Should().NotBe(op2.OperationId);
    }

    private static UpdateOperation NewOperation(
        UpdateStatus status,
        DateTimeOffset? completedAt,
        DateTimeOffset? startedAt = null) => new(
            OperationId: Guid.NewGuid(),
            Candidate: NewCandidate(),
            TargetSnapshot: NewDriver(),
            Status: status,
            ErrorMessage: null,
            BackupPath: null,
            RestorePointSequenceNumber: null,
            StartedAt: startedAt ?? DateTimeOffset.UtcNow,
            CompletedAt: completedAt);

    private static UpdateCandidate NewCandidate() => new(
        ForHardwareId: "PCI\\VEN_8086&DEV_1234",
        Source: UpdateSource.WindowsUpdate,
        NewVersion: new Version(2, 0, 0, 0),
        NewDate: new DateOnly(2026, 1, 1),
        DownloadUrl: new Uri("https://example.com/x.cab"),
        SizeBytes: 1024,
        KbArticle: null,
        IsSuperseded: false,
        SourceUpdateId: Guid.NewGuid().ToString(),
        SupersededIds: Array.Empty<string>());

    private static DriverInfo NewDriver() => DriverInfo.Empty("PCI\\VEN_8086&DEV_1234");
}
