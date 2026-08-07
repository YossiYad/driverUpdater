using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using DriverUpdater.Core.Results;
using DriverUpdater.Services.Install;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriverUpdater.Services.Tests.Install;

public class DriverDowngradeServiceTests
{
    private static DriverInfo NewDriver(string inf = "oem20.inf", string version = "2.0.0.0") => new(
        DeviceId: @"PCI\VEN_10DE&DEV_2504\4&1",
        HardwareId: @"PCI\VEN_10DE&DEV_2504",
        DeviceName: "NVIDIA GeForce RTX 3060",
        Category: DriverCategory.Display,
        Provider: "NVIDIA",
        Manufacturer: "NVIDIA",
        CurrentVersion: Version.Parse(version),
        CurrentDate: new DateOnly(2025, 5, 1),
        InfName: inf,
        InfPath: null,
        IsSigned: true,
        DeviceClass: "Display");

    private static DriverVersionRecord NewTarget(string version = "1.0.0.0", string? inf = "oem10.inf") => new(
        DeviceId: @"PCI\VEN_10DE&DEV_2504\4&1",
        DeviceName: "NVIDIA GeForce RTX 3060",
        Version: version,
        DriverDate: new DateOnly(2024, 9, 1),
        InfName: inf,
        Provider: "NVIDIA",
        FirstSeenAt: DateTimeOffset.UtcNow.AddMonths(-3),
        LastSeenAt: DateTimeOffset.UtcNow.AddMonths(-1));

    [Fact]
    public async Task Downgrade_backs_up_removes_newer_package_and_verifies_the_bound_version()
    {
        var pnputil = new RecordingPnPUtil();
        var service = NewService(
            pnputil,
            storePackages: new[] { Package("oem10.inf") },
            probedVersion: "1.0.0.0");

        var result = await service.DowngradeAsync(NewDriver(), NewTarget());

        result.IsSuccess.Should().BeTrue();
        result.Value.VerifiedDowngraded.Should().BeTrue();
        result.Value.BoundVersionAfter.Should().Be("1.0.0.0");
        result.Value.BackupFolderPath.Should().Be("backup-path");
        pnputil.Commands.Should().ContainInOrder(
            "/delete-driver \"oem20.inf\" /uninstall /force",
            "/scan-devices");
    }

    [Fact]
    public async Task Downgrade_fails_before_touching_anything_when_target_left_the_store()
    {
        var pnputil = new RecordingPnPUtil();
        var service = NewService(pnputil, storePackages: Array.Empty<DriverStorePackage>(), probedVersion: "2.0.0.0");

        var result = await service.DowngradeAsync(NewDriver(), NewTarget());

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("DOWNGRADE_TARGET_MISSING");
        pnputil.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task Downgrade_refuses_to_remove_an_inbox_driver()
    {
        var pnputil = new RecordingPnPUtil();
        var service = NewService(pnputil, storePackages: new[] { Package("oem10.inf") }, probedVersion: "2.0.0.0");

        var result = await service.DowngradeAsync(NewDriver(inf: "nv_dispi.inf"), NewTarget());

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("DOWNGRADE_INBOX_DRIVER");
        pnputil.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task Downgrade_reports_unverified_when_the_device_still_binds_the_newer_version()
    {
        var pnputil = new RecordingPnPUtil();
        var service = NewService(pnputil, storePackages: new[] { Package("oem10.inf") }, probedVersion: "2.0.0.0");

        var result = await service.DowngradeAsync(NewDriver(), NewTarget());

        result.IsSuccess.Should().BeTrue();
        result.Value.VerifiedDowngraded.Should().BeFalse();
        result.Value.BoundVersionAfter.Should().Be("2.0.0.0");
    }

    [Fact]
    public async Task Downgrade_stops_when_the_backup_fails()
    {
        var pnputil = new RecordingPnPUtil();
        var service = NewService(
            pnputil,
            storePackages: new[] { Package("oem10.inf") },
            probedVersion: "2.0.0.0",
            backupSucceeds: false);

        var result = await service.DowngradeAsync(NewDriver(), NewTarget());

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("DOWNGRADE_BACKUP_FAILED");
        pnputil.Commands.Should().BeEmpty();
    }

    private static DriverStorePackage Package(string published) =>
        new(published, "orig.inf", "NVIDIA", "Display", "1.0.0.0", new DateOnly(2024, 9, 1));

    private static DriverDowngradeService NewService(
        RecordingPnPUtil pnputil,
        IReadOnlyList<DriverStorePackage> storePackages,
        string probedVersion,
        bool backupSucceeds = true) =>
        new(new StubStoreBrowser(storePackages),
            new StubBackupService(backupSucceeds),
            pnputil,
            new StubProbe(probedVersion),
            NullLogger<DriverDowngradeService>.Instance);

    private sealed class StubStoreBrowser : IDriverStoreBrowser
    {
        private readonly IReadOnlyList<DriverStorePackage> _packages;
        public StubStoreBrowser(IReadOnlyList<DriverStorePackage> packages) => _packages = packages;
        public Task<IReadOnlyList<DriverStorePackage>> EnumeratePackagesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_packages);
    }

    private sealed class StubBackupService : IBackupService
    {
        private readonly bool _succeeds;
        public StubBackupService(bool succeeds) => _succeeds = succeeds;

        public Task<Result<BackupArtifact>> BackupDriverAsync(DriverInfo driver, CancellationToken cancellationToken = default) =>
            Task.FromResult(_succeeds
                ? Result<BackupArtifact>.Success(new BackupArtifact(
                    driver.InfName ?? string.Empty, driver.DeviceName, "backup-path", DateTimeOffset.UtcNow, 123))
                : Result<BackupArtifact>.Failure("BACKUP_PNPUTIL_FAILED", "export failed"));

        public Task<Result<bool>> RestoreFromBackupAsync(BackupArtifact artifact, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<bool>.Success(true));

        public IReadOnlyList<BackupArtifact> ListBackups() => Array.Empty<BackupArtifact>();

        public int PurgeBackupsOlderThan(TimeSpan age) => 0;
    }

    private sealed class RecordingPnPUtil : IPnPUtilRunner
    {
        public List<string> Commands { get; } = new();

        public Task<ProcessResult> RunAsync(string arguments, CancellationToken cancellationToken = default)
        {
            Commands.Add(arguments);
            return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
        }
    }

    private sealed class StubProbe : IInstalledDriverProbe
    {
        private readonly string _version;
        public StubProbe(string version) => _version = version;

        public Task<InstalledDriverState?> GetCurrentAsync(string deviceId, CancellationToken cancellationToken = default) =>
            Task.FromResult<InstalledDriverState?>(new InstalledDriverState(Version.Parse(_version), null));
    }
}
