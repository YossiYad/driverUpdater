using System.Runtime.CompilerServices;
using DriverUpdater.App.Tests.Stubs;
using DriverUpdater.App.ViewModels;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace DriverUpdater.App.Tests.ViewModels;

public class MainViewModelRunSummaryTests
{
    [WpfFact]
    public async Task Run_summary_reports_the_version_windows_actually_reports_after_the_install()
    {
        var logger = new CapturingLogger();
        var vm = NewVm(new VerifyingPipeline(new InstalledDriverState(new Version(32, 0, 16, 1088), null)), logger);
        AddConfirmedOutdatedRow(vm);

        await vm.UpdateAllCommand.ExecuteAsync(null);

        var summary = logger.Messages.Should().ContainSingle(m => m.Contains("Update run summary")).Subject;
        summary.Should().Contain("1.0.0.0 → 32.0.16.1088 (package 610.88)");
        summary.Should().NotContain("2026.7.28.0");
    }

    [WpfFact]
    public async Task Run_summary_falls_back_to_the_package_label_when_verification_read_back_nothing()
    {
        var logger = new CapturingLogger();
        var vm = NewVm(new VerifyingPipeline(null), logger);
        AddConfirmedOutdatedRow(vm);

        await vm.UpdateAllCommand.ExecuteAsync(null);

        var summary = logger.Messages.Should().ContainSingle(m => m.Contains("Update run summary")).Subject;
        summary.Should().Contain("1.0.0.0 → 610.88");
        summary.Should().NotContain("package 610.88");
    }

    private static void AddConfirmedOutdatedRow(MainViewModel vm)
    {
        var driver = new DriverInfo(
            DeviceId: "ID\\GPU",
            HardwareId: "PCI\\VEN_10DE&DEV_2489",
            DeviceName: "NVIDIA GeForce RTX 3060 Ti",
            Category: DriverCategory.Display,
            Provider: "NVIDIA",
            Manufacturer: "NVIDIA",
            CurrentVersion: new Version(1, 0, 0, 0),
            CurrentDate: new DateOnly(2024, 1, 1),
            InfName: "nv_dispi.inf",
            InfPath: null,
            IsSigned: true,
            DeviceClass: "Display");
        var candidate = new UpdateCandidate(
            ForHardwareId: driver.HardwareId,
            Source: UpdateSource.Oem,
            NewVersion: new Version(2026, 7, 28, 0),
            NewDate: new DateOnly(2026, 7, 28),
            DownloadUrl: new Uri("https://us.download.nvidia.com/Windows/610.88/610.88-desktop.exe"),
            SizeBytes: 1024,
            KbArticle: null,
            IsSuperseded: false,
            SourceUpdateId: "vendor-installer:nvidia:610.88",
            SupersededIds: Array.Empty<string>(),
            InstallKind: UpdateInstallKind.VendorInstaller,
            Confidence: UpdateConfidence.Confirmed,
            VersionLabel: "610.88");
        vm.Drivers.Add(new DriverRowViewModel(driver)
        {
            Status = DriverStatus.Outdated,
            AvailableUpdate = candidate
        });
        vm.ScannedCount = vm.Drivers.Count;
    }

    private static MainViewModel NewVm(IInstallPipeline pipeline, ILogger<MainViewModel> logger) =>
        new(new FakeScanService(),
            Array.Empty<IUpdateSource>(),
            new NullOemDetectionService(),
            pipeline,
            new AcceptingInstallConfirmation(),
            new NullHistoryWindowOpener(),
            new NullSettingsWindowOpener(),
            new NullLogsWindowOpener(),
            logger);

    private sealed class AcceptingInstallConfirmation : IInstallConfirmation
    {
        public InstallOptions? Confirm(UpdateOperation operation, bool dryRun) =>
            new(CreateRestorePoint: false, BackupCurrentDriver: false, DryRun: dryRun);
    }

    private sealed class VerifyingPipeline : IInstallPipeline
    {
        private readonly InstalledDriverState? _verified;

        public VerifyingPipeline(InstalledDriverState? verified) => _verified = verified;

        public Task<UpdateOperation> ExecuteAsync(
            UpdateOperation operation, InstallOptions options,
            IProgress<UpdateOperation>? progress = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(operation with
            {
                Status = UpdateStatus.Succeeded,
                ErrorMessage = null,
                CompletedAt = DateTimeOffset.UtcNow,
                VerifiedState = _verified
            });
    }

    private sealed class CapturingLogger : ILogger<MainViewModel>
    {
        public List<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            Messages.Add(formatter(state, exception));
        }
    }

    private sealed class FakeScanService : IDriverScanService
    {
        public async IAsyncEnumerable<DriverInfo> ScanAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
