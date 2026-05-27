using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using DriverUpdater.Core.Results;
using DriverUpdater.Services.Install;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriverUpdater.Services.Tests.Install;

public class InstallPipelineTests
{
    [Fact]
    public async Task ExecuteAsync_dry_run_returns_skipped_with_summary_and_does_not_call_services()
    {
        var rp = new FakeRestorePointService();
        var bk = new FakeBackupService();
        var wu = new FakeWuApiClient();
        var pipeline = new InstallPipeline(rp, bk, wu, NullLogger<InstallPipeline>.Instance);

        var op = NewOperation();
        var result = await pipeline.ExecuteAsync(op, new InstallOptions(DryRun: true));

        result.Status.Should().Be(UpdateStatus.Skipped);
        result.ErrorMessage.Should().Contain("Create system restore point");
        result.ErrorMessage.Should().Contain("Download from WindowsUpdate");
        rp.Invocations.Should().Be(0);
        bk.BackupInvocations.Should().Be(0);
        wu.DownloadAndInstallInvocations.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_skips_restore_point_when_option_disabled()
    {
        var rp = new FakeRestorePointService();
        var bk = new FakeBackupService();
        var wu = new FakeWuApiClient { InstallResult = new WuInstallResult(0, false, "ok") };
        var pipeline = new InstallPipeline(rp, bk, wu, NullLogger<InstallPipeline>.Instance);

        var op = NewOperation();
        var result = await pipeline.ExecuteAsync(op, new InstallOptions(CreateRestorePoint: false, BackupCurrentDriver: false));

        result.Status.Should().Be(UpdateStatus.Succeeded);
        rp.Invocations.Should().Be(0);
        bk.BackupInvocations.Should().Be(0);
        wu.DownloadAndInstallInvocations.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_full_happy_path_runs_restore_then_backup_then_install()
    {
        var rp = new FakeRestorePointService();
        var bk = new FakeBackupService();
        var wu = new FakeWuApiClient { InstallResult = new WuInstallResult(0, false, "ok") };
        var pipeline = new InstallPipeline(rp, bk, wu, NullLogger<InstallPipeline>.Instance);

        var statuses = new List<UpdateStatus>();
        var progress = new Progress<UpdateOperation>(o => statuses.Add(o.Status));

        var op = NewOperation();
        var result = await pipeline.ExecuteAsync(op, new InstallOptions(), progress);

        result.Status.Should().Be(UpdateStatus.Succeeded);
        result.RestorePointSequenceNumber.Should().Be("42");
        result.BackupPath.Should().NotBeNullOrEmpty();
        statuses.Should().ContainInOrder(
            UpdateStatus.CreatingRestorePoint,
            UpdateStatus.BackingUp,
            UpdateStatus.Downloading,
            UpdateStatus.Installing,
            UpdateStatus.Succeeded);
    }

    [Fact]
    public async Task ExecuteAsync_reports_reboot_required_in_error_message()
    {
        var wu = new FakeWuApiClient { InstallResult = new WuInstallResult(0, true, "ok") };
        var pipeline = new InstallPipeline(new FakeRestorePointService(), new FakeBackupService(), wu, NullLogger<InstallPipeline>.Instance);

        var result = await pipeline.ExecuteAsync(NewOperation(), new InstallOptions(CreateRestorePoint: false, BackupCurrentDriver: false));

        result.Status.Should().Be(UpdateStatus.Succeeded);
        result.ErrorMessage.Should().Contain("Reboot");
    }

    [Fact]
    public async Task ExecuteAsync_returns_failure_when_restore_point_fails()
    {
        var rp = new FakeRestorePointService { Failure = ResultError.From("RESTORE_POINT_FAILED", "denied") };
        var bk = new FakeBackupService();
        var wu = new FakeWuApiClient();
        var pipeline = new InstallPipeline(rp, bk, wu, NullLogger<InstallPipeline>.Instance);

        var result = await pipeline.ExecuteAsync(NewOperation(), new InstallOptions());

        result.Status.Should().Be(UpdateStatus.Failed);
        result.ErrorMessage.Should().Contain("denied");
        bk.BackupInvocations.Should().Be(0);
        wu.DownloadAndInstallInvocations.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_returns_failure_when_backup_fails()
    {
        var bk = new FakeBackupService { BackupFailure = ResultError.From("BACKUP_PNPUTIL_FAILED", "permission denied") };
        var wu = new FakeWuApiClient();
        var pipeline = new InstallPipeline(new FakeRestorePointService(), bk, wu, NullLogger<InstallPipeline>.Instance);

        var result = await pipeline.ExecuteAsync(NewOperation(), new InstallOptions());

        result.Status.Should().Be(UpdateStatus.Failed);
        result.ErrorMessage.Should().Contain("permission denied");
        wu.DownloadAndInstallInvocations.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_returns_failure_when_install_fails()
    {
        var wu = new FakeWuApiClient { InstallFailure = ResultError.From("WU_INSTALL_FAILED", "HRESULT 0x80070005") };
        var pipeline = new InstallPipeline(new FakeRestorePointService(), new FakeBackupService(), wu, NullLogger<InstallPipeline>.Instance);

        var result = await pipeline.ExecuteAsync(NewOperation(), new InstallOptions(CreateRestorePoint: false, BackupCurrentDriver: false));

        result.Status.Should().Be(UpdateStatus.Failed);
        result.ErrorMessage.Should().Contain("HRESULT");
    }

    [Fact]
    public async Task ExecuteAsync_skips_catalog_candidates_with_explanation()
    {
        var pipeline = new InstallPipeline(
            new FakeRestorePointService(),
            new FakeBackupService(),
            new FakeWuApiClient(),
            NullLogger<InstallPipeline>.Instance);

        var op = NewOperation(UpdateSource.MicrosoftCatalog);
        var result = await pipeline.ExecuteAsync(op, new InstallOptions(CreateRestorePoint: false, BackupCurrentDriver: false));

        result.Status.Should().Be(UpdateStatus.Skipped);
        result.ErrorMessage.Should().Contain("MicrosoftCatalog");
    }

    private static UpdateOperation NewOperation(UpdateSource source = UpdateSource.WindowsUpdate)
    {
        var driver = new DriverInfo(
            DeviceId: "PCI\\X",
            HardwareId: "PCI\\X",
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
            ForHardwareId: "PCI\\X",
            Source: source,
            NewVersion: new Version(2, 0),
            NewDate: new DateOnly(2026, 1, 1),
            DownloadUrl: new Uri("https://example.com/x.cab"),
            SizeBytes: 1024,
            KbArticle: null,
            IsSuperseded: false,
            SourceUpdateId: "abc-123",
            SupersededIds: Array.Empty<string>());
        return UpdateOperation.NewPending(candidate, driver);
    }

    private sealed class FakeRestorePointService : IRestorePointService
    {
        public int Invocations { get; private set; }
        public ResultError? Failure { get; set; }

        public Task<bool> IsSystemRestoreEnabledAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<Result<RestorePointInfo>> CreateRestorePointAsync(string description, CancellationToken cancellationToken = default)
        {
            Invocations++;
            if (Failure is not null)
            {
                return Task.FromResult(Result<RestorePointInfo>.Failure(Failure));
            }
            return Task.FromResult<Result<RestorePointInfo>>(new RestorePointInfo("42", description, DateTimeOffset.UtcNow));
        }
    }

    private sealed class FakeBackupService : IBackupService
    {
        public int BackupInvocations { get; private set; }
        public ResultError? BackupFailure { get; set; }

        public Task<Result<BackupArtifact>> BackupDriverAsync(DriverInfo driver, CancellationToken cancellationToken = default)
        {
            BackupInvocations++;
            if (BackupFailure is not null)
            {
                return Task.FromResult(Result<BackupArtifact>.Failure(BackupFailure));
            }
            return Task.FromResult<Result<BackupArtifact>>(
                new BackupArtifact(driver.InfName ?? "", driver.DeviceName, $"C:\\Temp\\Backup\\{driver.DeviceName}", DateTimeOffset.UtcNow, 1234));
        }

        public Task<Result<bool>> RestoreFromBackupAsync(BackupArtifact artifact, CancellationToken cancellationToken = default) =>
            Task.FromResult<Result<bool>>(true);

        public IReadOnlyList<BackupArtifact> ListBackups() => Array.Empty<BackupArtifact>();

        public int PurgeBackupsOlderThan(TimeSpan age) => 0;
    }

    private sealed class FakeWuApiClient : IWuApiClient
    {
        public int DownloadAndInstallInvocations { get; private set; }
        public WuInstallResult? InstallResult { get; set; }
        public ResultError? InstallFailure { get; set; }

        public async IAsyncEnumerable<WuDriverUpdateRecord> SearchDriverUpdatesAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<Result<WuInstallResult>> DownloadAndInstallAsync(string updateId, CancellationToken cancellationToken = default)
        {
            DownloadAndInstallInvocations++;
            if (InstallFailure is not null)
            {
                return Task.FromResult(Result<WuInstallResult>.Failure(InstallFailure));
            }
            return Task.FromResult<Result<WuInstallResult>>(InstallResult ?? new WuInstallResult(0, false, "ok"));
        }
    }
}
