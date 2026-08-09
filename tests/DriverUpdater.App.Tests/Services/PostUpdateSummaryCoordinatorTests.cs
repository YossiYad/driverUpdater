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
    public async Task CompleteRun_with_any_restart_saves_the_whole_batch_without_opening_summary()
    {
        var restartOperation = NewOperation(requiresRestart: true);
        var completedOperation = NewOperation(requiresRestart: false);
        var verifier = new FakeVerifier(UpdateVerificationStatus.PendingRestart);
        var store = new MemoryStore();
        var startup = new FakeStartupService();
        var opener = new FakeWindowOpener();
        var coordinator = NewCoordinator(verifier, store, startup, opener, DateTimeOffset.MinValue);

        var callbackCalls = 0;
        var report = await coordinator.CompleteRunAsync(
            new[] { restartOperation, completedOperation },
            _ => callbackCalls++);

        report.Should().BeNull();
        store.Batch.Should().NotBeNull();
        store.Batch!.Operations.Select(operation => operation.OperationId).Should().BeEquivalentTo(
            new[] { restartOperation.OperationId, completedOperation.OperationId });
        startup.RegisterCalls.Should().Be(1);
        verifier.CallCount.Should().Be(0);
        callbackCalls.Should().Be(0);
        opener.Reports.Should().BeEmpty();
    }

    [Fact]
    public async Task CompleteRun_without_restart_verifies_and_opens_summary_immediately()
    {
        var operation = NewOperation(requiresRestart: false);
        var verifier = new FakeVerifier(UpdateVerificationStatus.VerifiedUpdated);
        var store = new MemoryStore();
        var startup = new FakeStartupService();
        var opener = new FakeWindowOpener();
        var coordinator = NewCoordinator(verifier, store, startup, opener, DateTimeOffset.MinValue);

        var callbackCalls = 0;
        var report = await coordinator.CompleteRunAsync(new[] { operation }, _ => callbackCalls++);

        report.Should().NotBeNull();
        report!.IsAfterRestart.Should().BeFalse();
        verifier.CallCount.Should().Be(1);
        callbackCalls.Should().Be(1);
        opener.Reports.Should().ContainSingle().Which.IsAfterRestart.Should().BeFalse();
        store.Batch.Should().BeNull();
        startup.RegisterCalls.Should().Be(0);
    }

    [Fact]
    public async Task Resume_does_nothing_until_computer_has_restarted()
    {
        var createdAt = new DateTimeOffset(2026, 7, 14, 12, 0, 0, TimeSpan.Zero);
        var verifier = new FakeVerifier(UpdateVerificationStatus.VerifiedUpdated);
        var firstOperation = NewOperation();
        var secondOperation = NewOperation(requiresRestart: false);
        var store = new MemoryStore
        {
            Batch = new PendingUpdateVerificationBatch(
                Guid.NewGuid(),
                createdAt,
                new[] { firstOperation, secondOperation })
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
        var firstOperation = NewOperation();
        var secondOperation = NewOperation(requiresRestart: false);
        var store = new MemoryStore
        {
            Batch = new PendingUpdateVerificationBatch(
                Guid.NewGuid(),
                createdAt,
                new[] { firstOperation, secondOperation })
        };
        var startup = new FakeStartupService();
        var opener = new FakeWindowOpener();
        var coordinator = NewCoordinator(verifier, store, startup, opener, createdAt.AddMinutes(5));

        await coordinator.ResumeAfterRestartAsync();

        verifier.CallCount.Should().Be(1);
        verifier.LastAfterRestart.Should().BeTrue();
        verifier.LastOperationCount.Should().Be(2);
        opener.Reports.Should().ContainSingle().Which.IsAfterRestart.Should().BeTrue();
        opener.Reports[0].Items.Should().HaveCount(2);
        store.Batch.Should().BeNull();
        startup.UnregisterCalls.Should().Be(1);
    }

    [Fact]
    public async Task Resume_records_a_reboot_required_success_that_did_not_change_the_driver()
    {
        var createdAt = new DateTimeOffset(2026, 7, 14, 12, 0, 0, TimeSpan.Zero);
        var operation = NewOperation(requiresRestart: true);
        var verifier = new FakeVerifier(UpdateVerificationStatus.NotUpdated, reportsUnchanged: true);
        var store = new MemoryStore
        {
            Batch = new PendingUpdateVerificationBatch(Guid.NewGuid(), createdAt, new[] { operation })
        };
        var ineffective = new RecordingIneffectiveUpdateStore();
        var coordinator = NewCoordinator(
            verifier,
            store,
            new FakeStartupService(),
            new FakeWindowOpener(),
            createdAt.AddMinutes(5),
            ineffective);

        await coordinator.ResumeAfterRestartAsync();

        ineffective.Records.Should().ContainSingle().Which.Should().Be(
            (operation.TargetSnapshot.DeviceId,
             operation.Candidate.NewVersion.ToString(),
             operation.TargetSnapshot.CurrentVersion?.ToString()));
    }

    [Fact]
    public async Task Resume_does_not_record_anything_when_the_driver_actually_changed()
    {
        var createdAt = new DateTimeOffset(2026, 7, 14, 12, 0, 0, TimeSpan.Zero);
        var operation = NewOperation(requiresRestart: true);
        var verifier = new FakeVerifier(UpdateVerificationStatus.VerifiedUpdated);
        var store = new MemoryStore
        {
            Batch = new PendingUpdateVerificationBatch(Guid.NewGuid(), createdAt, new[] { operation })
        };
        var ineffective = new RecordingIneffectiveUpdateStore();
        var coordinator = NewCoordinator(
            verifier,
            store,
            new FakeStartupService(),
            new FakeWindowOpener(),
            createdAt.AddMinutes(5),
            ineffective);

        await coordinator.ResumeAfterRestartAsync();

        ineffective.Records.Should().BeEmpty();
    }

    private static PostUpdateSummaryCoordinator NewCoordinator(
        IPostUpdateVerifier verifier,
        IPendingUpdateVerificationStore store,
        IPostRebootStartupService startup,
        IUpdateSummaryWindowOpener opener,
        DateTimeOffset bootTime,
        IIneffectiveUpdateStore? ineffectiveUpdateStore = null) =>
        new(
            verifier,
            store,
            startup,
            new FakeBootTimeProvider(bootTime),
            opener,
            new FakeLocalizationService(),
            ineffectiveUpdateStore ?? new RecordingIneffectiveUpdateStore(),
            NullLogger<PostUpdateSummaryCoordinator>.Instance);

    private static UpdateOperation NewOperation(bool requiresRestart = true)
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
            ErrorMessage = requiresRestart ? "Reboot required to complete installation." : null,
            CompletedAt = DateTimeOffset.UtcNow
        };
    }

    private sealed class RecordingIneffectiveUpdateStore : IIneffectiveUpdateStore
    {
        public List<(string DeviceId, string TargetVersion, string? InstalledVersion)> Records { get; } = new();

        public Task<IReadOnlyList<IneffectiveUpdateRecord>> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<IneffectiveUpdateRecord>>(Array.Empty<IneffectiveUpdateRecord>());

        public Task RecordAsync(
            string deviceId,
            string targetVersion,
            string? installedVersion,
            CancellationToken cancellationToken = default)
        {
            Records.Add((deviceId, targetVersion, installedVersion));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeVerifier : IPostUpdateVerifier
    {
        private readonly UpdateVerificationStatus _status;
        private readonly bool _reportsUnchanged;

        public FakeVerifier(UpdateVerificationStatus status, bool reportsUnchanged = false)
        {
            _status = status;
            _reportsUnchanged = reportsUnchanged;
        }

        public int CallCount { get; private set; }
        public bool LastAfterRestart { get; private set; }
        public int LastOperationCount { get; private set; }

        public Task<UpdateVerificationReport> VerifyAsync(
            PendingUpdateVerificationBatch batch,
            bool isAfterRestart,
            AppLanguage language,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastAfterRestart = isAfterRestart;
            LastOperationCount = batch.Operations.Count;
            var items = batch.Operations.Select(operation => new UpdateVerificationItem(
                    operation.OperationId,
                    operation.TargetSnapshot.DeviceName,
                    operation.TargetSnapshot.Category,
                    operation.TargetSnapshot.CurrentVersion,
                    operation.TargetSnapshot.CurrentDate,
                    operation.Candidate.NewVersion,
                    operation.Candidate.NewDate,
                    isAfterRestart
                        ? _reportsUnchanged ? operation.TargetSnapshot.CurrentVersion : operation.Candidate.NewVersion
                        : null,
                    isAfterRestart
                        ? _reportsUnchanged ? operation.TargetSnapshot.CurrentDate : operation.Candidate.NewDate
                        : null,
                    _status,
                    operation.ErrorMessage,
                    operation.Status,
                    operation.Candidate.InstallKind,
                    operation.Candidate.Confidence,
                    null))
                .ToArray();
            return Task.FromResult(new UpdateVerificationReport(
                batch.BatchId,
                batch.CreatedAt,
                isAfterRestart,
                items,
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
