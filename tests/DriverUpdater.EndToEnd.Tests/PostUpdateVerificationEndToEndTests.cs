using System.IO;
using DriverUpdater.App.Services;
using DriverUpdater.Core.Models;
using DriverUpdater.EndToEnd.Tests.Harness;
using DriverUpdater.Infrastructure.Cache;
using DriverUpdater.Services.Ai;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriverUpdater.EndToEnd.Tests;

/// <summary>
/// Covers the "did the update actually take?" flow across a reboot: the real
/// <see cref="PostUpdateVerifier"/> classifies each finished operation, the real
    /// <see cref="PostUpdateSummaryCoordinator"/> persists the whole run when any update needs a restart through the real
/// <see cref="JsonPendingUpdateVerificationStore"/>, and a second app session resumes and
/// verifies them once the machine has actually restarted.
/// </summary>
public sealed class PostUpdateVerificationEndToEndTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    private static readonly DriverInfo Gpu = new(
        DeviceId: @"PCI\VEN_10DE&DEV_2484\4&1a2b&0&0008",
        HardwareId: @"PCI\VEN_10DE&DEV_2484",
        DeviceName: "NVIDIA GeForce RTX 3070",
        Category: DriverCategory.Display,
        Provider: "NVIDIA",
        Manufacturer: "NVIDIA",
        CurrentVersion: new Version(31, 0, 15, 3623),
        CurrentDate: new DateOnly(2023, 5, 1),
        InfName: "oem3.inf",
        InfPath: null,
        IsSigned: true,
        DeviceClass: "DISPLAY");

    private static UpdateCandidate GpuCandidate => new(
        ForHardwareId: Gpu.HardwareId,
        Source: UpdateSource.MicrosoftCatalog,
        NewVersion: new Version(32, 0, 15, 6094),
        NewDate: new DateOnly(2024, 7, 15),
        DownloadUrl: new Uri("https://catalog.update.microsoft.com/download/nvidia.cab"),
        SizeBytes: 500_000_000,
        KbArticle: null,
        IsSuperseded: false,
        SourceUpdateId: "catalog-nvidia-32.0.15.6094",
        SupersededIds: Array.Empty<string>(),
        InstallKind: UpdateInstallKind.PnPUtilPackage);

    private static UpdateOperation Finished(UpdateStatus status, string? errorMessage, DriverInfo? target = null) =>
        UpdateOperation.NewPending(GpuCandidate, target ?? Gpu) with
        {
            Status = status,
            ErrorMessage = errorMessage,
            CompletedAt = DateTimeOffset.UtcNow
        };

    private JsonPendingUpdateVerificationStore NewPendingStore() =>
        new(NullLogger<JsonPendingUpdateVerificationStore>.Instance, _workspace.Path("pending-verification.json"));

    private static PostUpdateVerifier NewVerifier(ScriptedInstalledDriverProbe probe, ScriptedAiTextCompleter? ai = null) =>
        new(probe, ai ?? new ScriptedAiTextCompleter(isConfigured: false), NullLogger<PostUpdateVerifier>.Instance);

    private JsonIneffectiveUpdateStore NewIneffectiveStore() =>
        new(NullLogger<JsonIneffectiveUpdateStore>.Instance, _workspace.Path("ineffective-updates.json"));

    private (PostUpdateSummaryCoordinator Coordinator,
             RecordingUpdateSummaryWindowOpener Windows,
             RecordingPostRebootStartupService Startup,
             FixedBootTimeProvider BootTime,
             JsonPendingUpdateVerificationStore Store,
             JsonIneffectiveUpdateStore Ineffective)
        BuildCoordinator(ScriptedInstalledDriverProbe probe, DateTimeOffset bootTime, ScriptedAiTextCompleter? ai = null)
    {
        var store = NewPendingStore();
        var windows = new RecordingUpdateSummaryWindowOpener();
        var startup = new RecordingPostRebootStartupService();
        var boot = new FixedBootTimeProvider(bootTime);
        var ineffective = NewIneffectiveStore();
        var coordinator = new PostUpdateSummaryCoordinator(
            NewVerifier(probe, ai),
            store,
            startup,
            boot,
            windows,
            new FixedLocalizationService(),
            ineffective,
            NullLogger<PostUpdateSummaryCoordinator>.Instance);
        return (coordinator, windows, startup, boot, store, ineffective);
    }

    [Fact]
    public async Task An_update_that_really_landed_is_reported_as_verified()
    {
        var probe = new ScriptedInstalledDriverProbe()
            .Always(Gpu.DeviceId, new InstalledDriverState(new Version(32, 0, 15, 6094), new DateOnly(2024, 7, 15)));
        var (coordinator, windows, startup, _, store, _) = BuildCoordinator(probe, DateTimeOffset.UtcNow.AddHours(-2));

        var report = await coordinator.CompleteRunAsync(new[] { Finished(UpdateStatus.Succeeded, null) });

        report.Should().NotBeNull();
        report!.Items.Should().ContainSingle();
        report.Items[0].Status.Should().Be(UpdateVerificationStatus.VerifiedUpdated);
        report.Items[0].CurrentVersion.Should().Be(new Version(32, 0, 15, 6094));
        report.VerifiedCount.Should().Be(1);
        windows.Reports.Should().ContainSingle();
        startup.RegisterCount.Should().Be(0, "nothing is pending a restart");
        File.Exists(store.StorePath).Should().BeFalse();
    }

    [Fact]
    public async Task Shared_amd_graphics_package_verifies_each_display_adapter_independently()
    {
        var integrated = Gpu with
        {
            DeviceId = @"PCI\VEN_1002&DEV_13C0\4&1",
            HardwareId = @"PCI\VEN_1002&DEV_13C0",
            DeviceName = "AMD Radeon(TM) Graphics",
            Provider = "Advanced Micro Devices, Inc.",
            Manufacturer = "Advanced Micro Devices, Inc.",
            CurrentVersion = new Version(32, 0, 21043, 1005),
            CurrentDate = new DateOnly(2026, 2, 18)
        };
        var discrete = integrated with
        {
            DeviceId = @"PCI\VEN_1002&DEV_747E\6&2",
            HardwareId = @"PCI\VEN_1002&DEV_747E",
            DeviceName = "AMD Radeon RX 7700 XT",
            CurrentVersion = new Version(32, 0, 31021, 5001),
            CurrentDate = new DateOnly(2026, 5, 13)
        };
        var sharedCandidate = GpuCandidate with
        {
            Source = UpdateSource.Oem,
            DownloadUrl = new Uri("https://drivers.amd.com/drivers/whql-amd-software-adrenalin-edition-26.7.1-win11-b.exe"),
            SourceUpdateId = "vendor-installer:nullsoft:amd-radeon:26.7.1",
            InstallKind = UpdateInstallKind.VendorInstaller,
            NewVersion = new Version(2026, 7, 28, 0),
            NewDate = new DateOnly(2026, 7, 28)
        };
        var operations = new[]
        {
            UpdateOperation.NewPending(
                sharedCandidate with { ForHardwareId = integrated.HardwareId },
                integrated) with
            {
                Status = UpdateStatus.Succeeded,
                CompletedAt = DateTimeOffset.UtcNow
            },
            UpdateOperation.NewPending(
                sharedCandidate with { ForHardwareId = discrete.HardwareId },
                discrete) with
            {
                Status = UpdateStatus.Succeeded,
                CompletedAt = DateTimeOffset.UtcNow
            }
        };
        var probe = new ScriptedInstalledDriverProbe()
            .Always(
                integrated.DeviceId,
                new InstalledDriverState(integrated.CurrentVersion, integrated.CurrentDate))
            .Always(
                discrete.DeviceId,
                new InstalledDriverState(
                    new Version(32, 0, 31035, 1003),
                    new DateOnly(2026, 7, 24)));
        var (coordinator, windows, _, _, _, _) =
            BuildCoordinator(probe, DateTimeOffset.UtcNow.AddHours(-2));

        var report = await coordinator.CompleteRunAsync(operations);

        report.Should().NotBeNull();
        report!.Items.Should().HaveCount(2);
        report.Items.Single(item => item.DeviceName == integrated.DeviceName).Status
            .Should().Be(UpdateVerificationStatus.NotUpdated);
        report.Items.Single(item => item.DeviceName == discrete.DeviceName).Status
            .Should().Be(UpdateVerificationStatus.VerifiedUpdated);
        windows.Reports.Should().ContainSingle();
    }

    [Fact]
    public async Task An_update_needing_a_restart_is_saved_and_verified_after_the_machine_reboots()
    {
        var installedAt = DateTimeOffset.UtcNow;
        var secondDevice = Gpu with
        {
            DeviceId = @"PCI\VEN_8086&DEV_1234\SECOND",
            HardwareId = @"PCI\VEN_8086&DEV_1234",
            DeviceName = "Second device"
        };
        var probe = new ScriptedInstalledDriverProbe()
            .Always(Gpu.DeviceId, new InstalledDriverState(Gpu.CurrentVersion, Gpu.CurrentDate))
            .Always(secondDevice.DeviceId, new InstalledDriverState(GpuCandidate.NewVersion, GpuCandidate.NewDate));
        var (coordinator, windows, startup, _, store, _) =
            BuildCoordinator(probe, bootTime: installedAt.AddHours(-3));

        var report = await coordinator.CompleteRunAsync(
            new[]
            {
                Finished(UpdateStatus.Succeeded, "Reboot required to complete installation."),
                Finished(UpdateStatus.Succeeded, null, secondDevice)
            });

        report.Should().BeNull("no summary is produced before a required restart");
        windows.Reports.Should().BeEmpty();
        startup.RegisterCount.Should().Be(1, "the app must relaunch after the restart to finish verification");
        File.Exists(store.StorePath).Should().BeTrue();

        var saved = await NewPendingStore().LoadAsync();
        saved.Should().NotBeNull();
        saved!.Operations.Should().HaveCount(2, "the final summary must contain the complete update run");
        saved.Operations.Select(operation => operation.TargetSnapshot.DeviceId).Should().BeEquivalentTo(
            new[] { Gpu.DeviceId, secondDevice.DeviceId });
        saved.Operations.Should().OnlyContain(operation =>
            operation.Candidate.NewVersion == new Version(32, 0, 15, 6094));

        // --- next session, after the machine actually restarted ---
        var afterRebootProbe = new ScriptedInstalledDriverProbe()
            .Always(Gpu.DeviceId, new InstalledDriverState(new Version(32, 0, 15, 6094), new DateOnly(2024, 7, 15)))
            .Always(secondDevice.DeviceId, new InstalledDriverState(new Version(32, 0, 15, 6094), new DateOnly(2024, 7, 15)));
        var second = BuildCoordinator(afterRebootProbe, bootTime: DateTimeOffset.UtcNow.AddMinutes(1));

        await second.Coordinator.ResumeAfterRestartAsync();

        second.Windows.Reports.Should().ContainSingle();
        second.Windows.Reports[0].IsAfterRestart.Should().BeTrue();
        second.Windows.Reports[0].Items.Should().HaveCount(2);
        second.Windows.Reports[0].Items.Should().OnlyContain(item =>
            item.Status == UpdateVerificationStatus.VerifiedUpdated);
        second.Startup.UnregisterCount.Should().Be(1);
        File.Exists(store.StorePath).Should().BeFalse("the pending batch is consumed once verified");
    }

    [Fact]
    public async Task A_pending_batch_is_kept_until_the_machine_has_actually_restarted()
    {
        var probe = new ScriptedInstalledDriverProbe()
            .Always(Gpu.DeviceId, new InstalledDriverState(Gpu.CurrentVersion, Gpu.CurrentDate));
        var (coordinator, _, _, _, store, _) = BuildCoordinator(probe, bootTime: DateTimeOffset.UtcNow.AddHours(-3));
        await coordinator.CompleteRunAsync(
            new[] { Finished(UpdateStatus.Succeeded, "Reboot required to complete installation.") });

        // The user relaunched the app without rebooting: boot time is still older than the batch.
        var sameBoot = BuildCoordinator(probe, bootTime: DateTimeOffset.UtcNow.AddHours(-3));
        await sameBoot.Coordinator.ResumeAfterRestartAsync();

        sameBoot.Windows.Reports.Should().BeEmpty("nothing can be verified before the restart happens");
        sameBoot.Startup.UnregisterCount.Should().Be(0);
        File.Exists(store.StorePath).Should().BeTrue("the batch must survive until the restart");
    }

    [Fact]
    public async Task A_vendor_page_row_is_classified_as_needing_manual_action_and_costs_no_ai_request()
    {
        var probe = new ScriptedInstalledDriverProbe();
        var ai = new ScriptedAiTextCompleter(isConfigured: true, "should never be asked");
        var (coordinator, windows, _, _, _, _) = BuildCoordinator(probe, DateTimeOffset.UtcNow.AddHours(-2), ai);

        var operation = UpdateOperation.NewPending(
            GpuCandidate with
            {
                InstallKind = UpdateInstallKind.VendorPage,
                DownloadUrl = new Uri("https://www.nvidia.com/download/index.aspx")
            },
            Gpu) with
        {
            Status = UpdateStatus.Skipped,
            ErrorMessage = "Open the official vendor page to install this update: https://www.nvidia.com/download/index.aspx",
            CompletedAt = DateTimeOffset.UtcNow
        };

        var report = await coordinator.CompleteRunAsync(new[] { operation });

        report!.Items[0].Status.Should().Be(UpdateVerificationStatus.ManualActionRequired);
        report.Items[0].ActionUrl.Should().BeNull("the app never opens an external page; nothing is left to point at");
        report.ManualActionCount.Should().Be(1);
        report.AiWasUsed.Should().BeFalse();
        ai.Prompts.Should().BeEmpty("Gemini has a hard daily quota; a manual-only run has nothing to summarize");
        windows.Reports.Should().ContainSingle();
    }

    [Fact]
    public async Task An_install_that_windows_ignored_is_reported_as_not_updated_and_does_get_an_ai_summary()
    {
        var probe = new ScriptedInstalledDriverProbe()
            .Always(Gpu.DeviceId, new InstalledDriverState(Gpu.CurrentVersion, Gpu.CurrentDate));
        var ai = new ScriptedAiTextCompleter(isConfigured: true, "Windows kept the driver it already had.");
        var (coordinator, _, _, _, _, _) = BuildCoordinator(probe, DateTimeOffset.UtcNow.AddHours(-2), ai);

        var report = await coordinator.CompleteRunAsync(new[]
        {
            Finished(
                UpdateStatus.Skipped,
                "Installed to the driver store, but Windows kept the existing driver (version unchanged: 31.0.15.3623).")
        });

        report!.Items[0].Status.Should().Be(UpdateVerificationStatus.NotUpdated);
        report.AttentionCount.Should().Be(1);
        report.AiWasUsed.Should().BeTrue();
        report.AiSummary.Should().Be("Windows kept the driver it already had.");
        ai.Prompts.Should().ContainSingle();
        ai.Prompts[0].Should().Contain("NVIDIA GeForce RTX 3070");
    }

    [Fact]
    public async Task A_device_that_cannot_be_read_back_is_reported_as_inconclusive_not_as_success()
    {
        var probe = new ScriptedInstalledDriverProbe().Always(Gpu.DeviceId, null);
        var (coordinator, _, _, _, _, _) = BuildCoordinator(probe, DateTimeOffset.UtcNow.AddHours(-2));

        var report = await coordinator.CompleteRunAsync(new[] { Finished(UpdateStatus.Succeeded, null) });

        report!.Items[0].Status.Should().Be(UpdateVerificationStatus.Inconclusive);
        report.VerifiedCount.Should().Be(0);
    }

    [Fact]
    public async Task Two_runs_before_a_restart_accumulate_into_one_pending_batch()
    {
        var probe = new ScriptedInstalledDriverProbe()
            .Always(Gpu.DeviceId, new InstalledDriverState(Gpu.CurrentVersion, Gpu.CurrentDate));
        var bootTime = DateTimeOffset.UtcNow.AddHours(-3);

        var first = BuildCoordinator(probe, bootTime);
        await first.Coordinator.CompleteRunAsync(
            new[] { Finished(UpdateStatus.Succeeded, "Reboot required to complete installation.") });

        var otherDevice = Gpu with { DeviceId = @"PCI\VEN_8086&DEV_9A49\4&x&0", DeviceName = "Intel Iris Xe" };
        var probe2 = new ScriptedInstalledDriverProbe()
            .Always(Gpu.DeviceId, new InstalledDriverState(Gpu.CurrentVersion, Gpu.CurrentDate))
            .Always(otherDevice.DeviceId, new InstalledDriverState(otherDevice.CurrentVersion, otherDevice.CurrentDate));
        var second = BuildCoordinator(probe2, bootTime);
        await second.Coordinator.CompleteRunAsync(new[]
        {
            Finished(UpdateStatus.Succeeded, "Reboot required to complete installation.", otherDevice)
        });

        var saved = await NewPendingStore().LoadAsync();
        saved!.Operations.Should().HaveCount(
            2,
            "an update installed before the reboot must not be dropped by a later run in the same boot session");
        saved.Operations.Select(o => o.TargetSnapshot.DeviceId)
            .Should().Contain(new[] { Gpu.DeviceId, otherDevice.DeviceId });
    }
}
