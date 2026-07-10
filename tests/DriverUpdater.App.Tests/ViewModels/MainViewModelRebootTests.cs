using System.Runtime.CompilerServices;
using DriverUpdater.App.Services;
using DriverUpdater.App.Tests.Stubs;
using DriverUpdater.App.ViewModels;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriverUpdater.App.Tests.ViewModels;

public class MainViewModelRebootTests
{
    [WpfFact]
    public async Task Install_prompts_and_restarts_when_update_requires_reboot_and_user_accepts()
    {
        var prompt = new FakeRebootPrompt { Answer = true };
        var vm = NewVm(new RebootRequiredPipeline(), prompt);
        AddConfirmedOutdatedRow(vm, "Intel Iris Xe Graphics");

        await vm.InstallConfirmedCommand.ExecuteAsync(null);

        prompt.ConfirmCalls.Should().ContainSingle().Which.Should().Be(1);
        prompt.RestartCalled.Should().BeTrue();
    }

    [WpfFact]
    public async Task Install_prompts_but_does_not_restart_when_user_declines()
    {
        var prompt = new FakeRebootPrompt { Answer = false };
        var vm = NewVm(new RebootRequiredPipeline(), prompt);
        AddConfirmedOutdatedRow(vm, "Intel Iris Xe Graphics");

        await vm.InstallConfirmedCommand.ExecuteAsync(null);

        prompt.ConfirmCalls.Should().ContainSingle();
        prompt.RestartCalled.Should().BeFalse();
    }

    [WpfFact]
    public async Task Install_does_not_prompt_when_no_update_requires_reboot()
    {
        var prompt = new FakeRebootPrompt { Answer = true };
        var vm = NewVm(new NoRebootPipeline(), prompt);
        AddConfirmedOutdatedRow(vm, "Intel Iris Xe Graphics");

        await vm.InstallConfirmedCommand.ExecuteAsync(null);

        prompt.ConfirmCalls.Should().BeEmpty();
        prompt.RestartCalled.Should().BeFalse();
    }

    private static void AddConfirmedOutdatedRow(MainViewModel vm, string name)
    {
        var driver = new DriverInfo(
            DeviceId: $"ID\\{name}",
            HardwareId: $"HW\\{name}",
            DeviceName: name,
            Category: DriverCategory.Display,
            Provider: "Vendor",
            Manufacturer: "Vendor",
            CurrentVersion: new Version(1, 0, 0, 0),
            CurrentDate: new DateOnly(2024, 1, 1),
            InfName: "oem.inf",
            InfPath: null,
            IsSigned: true,
            DeviceClass: "Display");
        var candidate = new UpdateCandidate(
            ForHardwareId: driver.HardwareId,
            Source: UpdateSource.MicrosoftCatalog,
            NewVersion: new Version(2, 0, 0, 0),
            NewDate: new DateOnly(2026, 1, 1),
            DownloadUrl: new Uri("https://example.com/x.cab"),
            SizeBytes: 1024,
            KbArticle: null,
            IsSuperseded: false,
            SourceUpdateId: $"update-{name}",
            SupersededIds: Array.Empty<string>(),
            InstallKind: UpdateInstallKind.PnPUtilPackage,
            Confidence: UpdateConfidence.Confirmed);
        vm.Drivers.Add(new DriverRowViewModel(driver)
        {
            Status = DriverStatus.Outdated,
            AvailableUpdate = candidate
        });
        vm.ScannedCount = vm.Drivers.Count;
    }

    private static MainViewModel NewVm(IInstallPipeline pipeline, IRebootPrompt rebootPrompt) =>
        new(new FakeScanService(),
            Array.Empty<IUpdateSource>(),
            new NullOemDetectionService(),
            pipeline,
            new AcceptingInstallConfirmation(),
            new NullHistoryWindowOpener(),
            new NullSettingsWindowOpener(),
            new NullLogsWindowOpener(),
            NullLogger<MainViewModel>.Instance,
            rebootPrompt: rebootPrompt);

    private sealed class FakeRebootPrompt : IRebootPrompt
    {
        public bool Answer { get; set; }
        public List<int> ConfirmCalls { get; } = new();
        public bool RestartCalled { get; private set; }

        public bool ConfirmRestartNow(int rebootRequiredDriverCount)
        {
            ConfirmCalls.Add(rebootRequiredDriverCount);
            return Answer;
        }

        public void RestartNow() => RestartCalled = true;
    }

    private sealed class AcceptingInstallConfirmation : IInstallConfirmation
    {
        public InstallOptions? Confirm(UpdateOperation operation, bool dryRun) =>
            new(CreateRestorePoint: false, BackupCurrentDriver: false, DryRun: dryRun);
    }

    private sealed class RebootRequiredPipeline : IInstallPipeline
    {
        public Task<UpdateOperation> ExecuteAsync(
            UpdateOperation operation, InstallOptions options,
            IProgress<UpdateOperation>? progress = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(operation with
            {
                Status = UpdateStatus.Succeeded,
                ErrorMessage = "Reboot required to complete driver installation.",
                CompletedAt = DateTimeOffset.UtcNow
            });
    }

    private sealed class NoRebootPipeline : IInstallPipeline
    {
        public Task<UpdateOperation> ExecuteAsync(
            UpdateOperation operation, InstallOptions options,
            IProgress<UpdateOperation>? progress = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(operation with
            {
                Status = UpdateStatus.Succeeded,
                ErrorMessage = null,
                CompletedAt = DateTimeOffset.UtcNow
            });
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
