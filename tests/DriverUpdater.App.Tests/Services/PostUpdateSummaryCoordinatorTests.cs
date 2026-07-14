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
    public async Task CompleteRun_verifies_operations_and_opens_summary()
    {
        var operation = NewOperation();
        var verifier = new FakeVerifier();
        var opener = new FakeWindowOpener();
        var coordinator = new PostUpdateSummaryCoordinator(
            verifier,
            opener,
            new FakeLocalizationService(),
            NullLogger<PostUpdateSummaryCoordinator>.Instance);

        await coordinator.CompleteRunAsync(new[] { operation });

        verifier.CallCount.Should().Be(1);
        verifier.LastAfterRestart.Should().BeFalse();
        opener.Reports.Should().ContainSingle();
        opener.Reports[0].Items.Should().ContainSingle();
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
            CompletedAt = DateTimeOffset.UtcNow
        };
    }

    private sealed class FakeVerifier : IPostUpdateVerifier
    {
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
            var operation = batch.Operations.Single();
            var item = new UpdateVerificationItem(
                operation.OperationId,
                operation.TargetSnapshot.DeviceName,
                operation.TargetSnapshot.Category,
                operation.TargetSnapshot.CurrentVersion,
                operation.TargetSnapshot.CurrentDate,
                operation.Candidate.NewVersion,
                operation.Candidate.NewDate,
                operation.Candidate.NewVersion,
                operation.Candidate.NewDate,
                UpdateVerificationStatus.VerifiedUpdated,
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
