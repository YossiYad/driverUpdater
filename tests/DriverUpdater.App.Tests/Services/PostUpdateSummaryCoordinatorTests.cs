using DriverUpdater.App.Services;
using DriverUpdater.App.ViewModels;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriverUpdater.App.Tests.Services;

public class PostUpdateSummaryCoordinatorTests
{
    [Fact]
    public async Task CompleteRun_saves_pending_restart_registers_startup_and_opens_summary()
    {
        var operation = NewOperation();
        var verifier = new FakeVerifier(UpdateVerificationStatus.PendingRestart);
        var store = new MemoryStore();
        var startup = new FakeStartupService();
        var opener = new FakeWindowOpener();
        var coordinator = NewCoordinator(verifier, store, startup, opener, DateTimeOffset.MinValue);

        var report = await coordinator.CompleteRunAsync(new[] { operation });

        report.Should().NotBeNull();
        store.Batch.Should().NotBeNull();
        store.Batch!.Operations.Should().ContainSingle().Which.OperationId.Should().Be(operation.OperationId);
        startup.RegisterCalls.Should().Be(1);
        opener.Reports.Should().ContainSingle();
        opener.Reports[0].IsAfterRestart.Should().BeFalse();
    }

    [Fact]
    public async Task Resume_does_nothing_until_computer_has_restarted()
    {
        var createdAt = new DateTimeOffset(2026, 7, 14, 12, 0, 0, TimeSpan.Zero);
        var verifier = new FakeVerifier(UpdateVerificationStatus.VerifiedUpdated);
        var store = new MemoryStore
        {
            Batch = new PendingUpdateVerificationBatch(Guid.NewGuid(), createdAt, new[] { NewOperation() })
        };
        var startup = new FakeStartupService();
        var opener = new FakeWindowOpener();
        var coordinator = NewCoordinator(verifier, store, startup, opener, createdAt.AddHours(-2));

        await coordinator.ResumeAfterRestartAsync();

        verifier.CallCount.Should().Be(0);
        opener.Reports.Should().BeEmpty();
        store.Batch.Should().NotBeNull();
        startup.UnregisterCalls.Should().Be(0);
    }

    [Fact]
    public async Task Resume_after_restart_verifies_opens_summary_and_removes_startup()
    {
        var createdAt = new DateTimeOffset(2026, 7, 14, 12, 0, 0, TimeSpan.Zero);
        var verifier = new FakeVerifier(UpdateVerificationStatus.VerifiedUpdated);
        var store = new MemoryStore
        {
            Batch = new PendingUpdateVerificationBatch(Guid.NewGuid(), createdAt, new[] { NewOperation() })
        };
        var startup = new FakeStartupService();
        var opener = new FakeWindowOpener();
        var coordinator = NewCoordinator(verifier, store, startup, opener, createdAt.AddMinutes(5));

        await coordinator.ResumeAfterRestartAsync();

        verifier.CallCount.Should().Be(1);
        verifier.LastAfterRestart.Should().BeTrue();
        opener.Reports.Should().ContainSingle().Which.IsAfterRestart.Should().BeTrue();
        store.Batch.Should().BeNull();
        startup.UnregisterCalls.Should().Be(1);
    }

    private static PostUpdateSummaryCoordinator NewCoordinator(
        IPostUpdateVerifier verifier,
        IPendingUpdateVerificationStore store,
        IPostRebootStartupService startup,
        IUpdateSummaryWindowOpener opener,
        DateTimeOffset bootTime) =>
        new(
            verifier,
            store,
            startup,
            new FakeBootTimeProvider(bootTime),
            opener,
            new FakeLocalizationService(),
            NullLogger<PostUpdateSummaryCoordinator>.Instance);

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

    private sealed class FakeVerifier : IPostUpdateVerifier
    {
        private readonly UpdateVerificationStatus _status;

        public FakeVerifier(UpdateVerificationStatus status) => _status = status;

        public int CallCount { get; private set; }
        public bool LastAfterRestart { get; private set; }

        public Task<UpdateVerificationReport> VerifyAsync(
            PendingUpdateVerificationBatch batch,
            bool isAfterRestart,
            AppLanguage language,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastAfterRestart = isAfterRestart;
            var operation = batch.Operations[0];
            var item = new UpdateVerificationItem(
                operation.OperationId,
                operation.TargetSnapshot.DeviceName,
                operation.TargetSnapshot.Category,
                operation.TargetSnapshot.CurrentVersion,
                operation.TargetSnapshot.CurrentDate,
                operation.Candidate.NewVersion,
                operation.Candidate.NewDate,
                isAfterRestart ? operation.Candidate.NewVersion : null,
                isAfterRestart ? operation.Candidate.NewDate : null,
                _status,
                operation.ErrorMessage,
                operation.Status,
                operation.Candidate.InstallKind,
                operation.Candidate.Confidence,
                null);
            return Task.FromResult(new UpdateVerificationReport(
                batch.BatchId,
                batch.CreatedAt,
                isAfterRestart,
                new[] { item },
                "Simple summary",
                true));
        }
    }

    private sealed class MemoryStore : IPendingUpdateVerificationStore
    {
        public PendingUpdateVerificationBatch? Batch { get; set; }

        public Task SaveAsync(PendingUpdateVerificationBatch batch, CancellationToken cancellationToken = default)
        {
            Batch = batch;
            return Task.CompletedTask;
        }

        public Task<PendingUpdateVerificationBatch?> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Batch);

        public Task ClearAsync(CancellationToken cancellationToken = default)
        {
            Batch = null;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeStartupService : IPostRebootStartupService
    {
        public int RegisterCalls { get; private set; }
        public int UnregisterCalls { get; private set; }

        public Task RegisterAsync(CancellationToken cancellationToken = default)
        {
            RegisterCalls++;
            return Task.CompletedTask;
        }

        public Task UnregisterAsync(CancellationToken cancellationToken = default)
        {
            UnregisterCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeBootTimeProvider : ISystemBootTimeProvider
    {
        private readonly DateTimeOffset _bootTime;

        public FakeBootTimeProvider(DateTimeOffset bootTime) => _bootTime = bootTime;

        public DateTimeOffset GetBootTimeUtc() => _bootTime;
    }

    private sealed class FakeWindowOpener : IUpdateSummaryWindowOpener
    {
        public List<UpdateVerificationReport> Reports { get; } = new();

        public void Open(UpdateVerificationReport report, AppLanguage language) => Reports.Add(report);
    }

    private sealed class FakeLocalizationService : ILocalizationService
    {
        public AppLanguage CurrentLanguage => AppLanguage.English;
        public bool IsRightToLeft => false;
        public event EventHandler? LanguageChanged;
        public void ApplyLanguage(AppLanguage language) => LanguageChanged?.Invoke(this, EventArgs.Empty);
    }
}
