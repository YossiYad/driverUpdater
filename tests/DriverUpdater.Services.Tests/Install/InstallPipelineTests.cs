using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using DriverUpdater.Core.Results;
using DriverUpdater.Services.Install;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Text.RegularExpressions;

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
        var progress = new RecordingProgress(o => statuses.Add(o.Status));

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

    [Fact]
    public async Task ExecuteAsync_installs_catalog_cab_package_with_pnputil()
    {
        var pnputil = new FakePnPUtilRunner();
        var powerShell = new FakePowerShellInvoker();
        var http = new FakeHttpClientFactory(new byte[] { 1, 2, 3 });
        var pipeline = new InstallPipeline(
            new FakeRestorePointService(),
            new FakeBackupService(),
            new FakeWuApiClient(),
            NullLogger<InstallPipeline>.Instance,
            pnputil,
            powerShell,
            httpClientFactory: http);

        var result = await pipeline.ExecuteAsync(
            NewOperation(UpdateSource.MicrosoftCatalog, UpdateInstallKind.PnPUtilPackage, new Uri("https://download.example.com/driver.cab")),
            new InstallOptions(CreateRestorePoint: false, BackupCurrentDriver: false));

        result.Status.Should().Be(UpdateStatus.Succeeded);
        http.RequestedUris.Should().ContainSingle().Which.Should().Be(new Uri("https://download.example.com/driver.cab"));
        powerShell.Invocations.Should().ContainSingle(s => s.Contains("expand.exe", StringComparison.OrdinalIgnoreCase));
        pnputil.Arguments.Should().ContainSingle();
        pnputil.Arguments[0].Should().Contain("/add-driver");
        pnputil.Arguments[0].Should().Contain("*.inf");
        pnputil.Arguments[0].Should().Contain("/install");
    }

    [Fact]
    public async Task ExecuteAsync_installs_vendor_msi_with_msiexec()
    {
        var vendorInstaller = new FakeVendorInstallerRunner();
        var http = new FakeHttpClientFactory(new byte[] { 1, 2, 3 });
        var pipeline = new InstallPipeline(
            new FakeRestorePointService(),
            new FakeBackupService(),
            new FakeWuApiClient(),
            NullLogger<InstallPipeline>.Instance,
            vendorInstallerRunner: vendorInstaller,
            httpClientFactory: http);

        var result = await pipeline.ExecuteAsync(
            NewOperation(UpdateSource.Oem, UpdateInstallKind.VendorInstaller, new Uri("https://download.example.com/driver.msi")),
            new InstallOptions(CreateRestorePoint: false, BackupCurrentDriver: false));

        result.Status.Should().Be(UpdateStatus.Succeeded);
        vendorInstaller.Invocations.Should().ContainSingle();
        vendorInstaller.Invocations[0].FileName.Should().EndWith("msiexec.exe");
        vendorInstaller.Invocations[0].Arguments.Should().Contain("/qn");
        vendorInstaller.Invocations[0].Arguments.Should().Contain("/norestart");
    }

    [Fact]
    public async Task ExecuteAsync_prefers_signed_inf_installation_for_vendor_zip()
    {
        var pnputil = new FakePnPUtilRunner();
        var vendorInstaller = new FakeVendorInstallerRunner();
        var pipeline = new InstallPipeline(
            new FakeRestorePointService(),
            new FakeBackupService(),
            new FakeWuApiClient(),
            NullLogger<InstallPipeline>.Instance,
            pnputil: pnputil,
            vendorInstallerRunner: vendorInstaller,
            httpClientFactory: new FakeHttpClientFactory(CreateDriverZip()));

        var op = NewOperation(
            UpdateSource.Oem,
            UpdateInstallKind.VendorInstaller,
            new Uri("https://download.gigabyte.com/FileList/Driver/audio.zip"));
        op = op with
        {
            Candidate = op.Candidate with
            {
                SourceUpdateId = "vendor-installer:installshield:gigabyte:b850:audio:1.0"
            }
        };

        var result = await pipeline.ExecuteAsync(
            op,
            new InstallOptions(CreateRestorePoint: false, BackupCurrentDriver: false));

        result.Status.Should().Be(UpdateStatus.Succeeded);
        pnputil.Arguments.Should().ContainSingle()
            .Which.Should().Contain("*.inf").And.Contain("/subdirs").And.Contain("/install");
        vendorInstaller.Invocations.Should().BeEmpty("INF installation is safer and more deterministic than an opaque setup wrapper");
    }

    [Fact]
    public async Task ExecuteAsync_fails_when_approved_vendor_exe_is_not_a_valid_pe()
    {
        var vendorInstaller = new FakeVendorInstallerRunner();
        var pipeline = new InstallPipeline(
            new FakeRestorePointService(),
            new FakeBackupService(),
            new FakeWuApiClient(),
            NullLogger<InstallPipeline>.Instance,
            vendorInstallerRunner: vendorInstaller,
            httpClientFactory: new FakeHttpClientFactory(System.Text.Encoding.UTF8.GetBytes("<html>not a binary</html>")));

        var op = NewOperation(UpdateSource.Oem, UpdateInstallKind.VendorInstaller, new Uri("https://download.example.com/driver.exe"));
        op = op with
        {
            Candidate = op.Candidate with { SourceUpdateId = "vendor-installer:installshield:amd-chipset:8.05" }
        };

        var result = await pipeline.ExecuteAsync(op, new InstallOptions(CreateRestorePoint: false, BackupCurrentDriver: false));

        result.Status.Should().Be(UpdateStatus.Failed);
        result.ErrorMessage.Should().Contain("HTML page");
        vendorInstaller.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_skips_unapproved_vendor_exe()
    {
        var vendorInstaller = new FakeVendorInstallerRunner();
        var pipeline = new InstallPipeline(
            new FakeRestorePointService(),
            new FakeBackupService(),
            new FakeWuApiClient(),
            NullLogger<InstallPipeline>.Instance,
            vendorInstallerRunner: vendorInstaller,
            httpClientFactory: new FakeHttpClientFactory(new byte[] { 1, 2, 3 }));

        var result = await pipeline.ExecuteAsync(
            NewOperation(UpdateSource.Oem, UpdateInstallKind.VendorInstaller, new Uri("https://download.example.com/driver.exe")),
            new InstallOptions(CreateRestorePoint: false, BackupCurrentDriver: false));

        result.Status.Should().Be(UpdateStatus.Skipped);
        result.ErrorMessage.Should().Contain("not approved");
        vendorInstaller.Invocations.Should().BeEmpty();
    }

    private static UpdateOperation NewOperation(
        UpdateSource source = UpdateSource.WindowsUpdate,
        UpdateInstallKind installKind = UpdateInstallKind.WindowsUpdate,
        Uri? downloadUrl = null)
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
            DownloadUrl: downloadUrl ?? new Uri("https://example.com/x.cab"),
            SizeBytes: 1024,
            KbArticle: null,
            IsSuperseded: false,
            SourceUpdateId: "abc-123",
            SupersededIds: Array.Empty<string>(),
            InstallKind: installKind);
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

    private sealed class RecordingProgress : IProgress<UpdateOperation>
    {
        private readonly Action<UpdateOperation> _onReport;

        public RecordingProgress(Action<UpdateOperation> onReport)
        {
            _onReport = onReport;
        }

        public void Report(UpdateOperation value) => _onReport(value);
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

    private sealed class FakePnPUtilRunner : IPnPUtilRunner
    {
        public List<string> Arguments { get; } = new();

        public Task<ProcessResult> RunAsync(string arguments, CancellationToken cancellationToken = default)
        {
            Arguments.Add(arguments);
            return Task.FromResult(new ProcessResult(0, "ok", ""));
        }
    }

    private sealed class FakePowerShellInvoker : IPowerShellInvoker
    {
        public List<string> Invocations { get; } = new();

        public Task<ProcessResult> InvokeAsync(string script, CancellationToken cancellationToken = default)
        {
            Invocations.Add(script);
            var matches = Regex.Matches(script, "'(?<path>[^']*)'");
            var extractDir = matches[^1].Groups["path"].Value;
            Directory.CreateDirectory(extractDir);
            File.WriteAllText(Path.Combine(extractDir, "driver.inf"), "[Version]");
            return Task.FromResult(new ProcessResult(0, "expanded", ""));
        }
    }

    private sealed class FakeVendorInstallerRunner : IVendorInstallerRunner
    {
        public List<(string FileName, string Arguments)> Invocations { get; } = new();

        public Task<ProcessResult> RunAsync(string fileName, string arguments, CancellationToken cancellationToken = default)
        {
            Invocations.Add((fileName, arguments));
            return Task.FromResult(new ProcessResult(0, "ok", ""));
        }
    }

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        private readonly byte[] _content;

        public FakeHttpClientFactory(byte[] content)
        {
            _content = content;
        }

        public List<Uri> RequestedUris { get; } = new();

        public HttpClient CreateClient(string name) => new(new Handler(_content, RequestedUris));

        private sealed class Handler : HttpMessageHandler
        {
            private readonly byte[] _content;
            private readonly List<Uri> _requestedUris;

            public Handler(byte[] content, List<Uri> requestedUris)
            {
                _content = content;
                _requestedUris = requestedUris;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                _requestedUris.Add(request.RequestUri!);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(_content)
                });
            }
        }
    }

    [Fact]
    public void LocateInstallerInTree_finds_setup_exe_at_root()
    {
        using var temp = new TempDir();
        File.WriteAllText(Path.Combine(temp.Path, "Setup.exe"), "stub");
        File.WriteAllText(Path.Combine(temp.Path, "ReadMe.txt"), "stub");

        var located = InstallPipeline.LocateInstallerInTree(temp.Path);

        located.Should().EndWith("Setup.exe");
    }

    [Fact]
    public void LocateInstallerInTree_prefers_shallow_setup_over_nested_exe()
    {
        using var temp = new TempDir();
        File.WriteAllText(Path.Combine(temp.Path, "Setup.exe"), "stub");
        Directory.CreateDirectory(Path.Combine(temp.Path, "x86"));
        File.WriteAllText(Path.Combine(temp.Path, "x86", "Install.exe"), "stub");

        var located = InstallPipeline.LocateInstallerInTree(temp.Path);

        located.Should().EndWith("Setup.exe");
    }

    [Fact]
    public void LocateInstallerInTree_falls_back_to_msi_and_then_first_exe()
    {
        using var temp = new TempDir();
        File.WriteAllText(Path.Combine(temp.Path, "vendor.msi"), "stub");

        InstallPipeline.LocateInstallerInTree(temp.Path).Should().EndWith("vendor.msi");

        File.Delete(Path.Combine(temp.Path, "vendor.msi"));
        File.WriteAllText(Path.Combine(temp.Path, "Other.exe"), "stub");
        InstallPipeline.LocateInstallerInTree(temp.Path).Should().EndWith("Other.exe");
    }

    [Fact]
    public void LocateInstallerInTree_returns_null_when_no_installer_found()
    {
        using var temp = new TempDir();
        File.WriteAllText(Path.Combine(temp.Path, "ReadMe.txt"), "stub");

        InstallPipeline.LocateInstallerInTree(temp.Path).Should().BeNull();
    }

    [Fact]
    public void ExtractZipAndLocateInstaller_extracts_and_finds_setup()
    {
        using var temp = new TempDir();
        var zipPath = Path.Combine(temp.Path, "driver.zip");
        using (var zip = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("Setup.exe");
            using var stream = entry.Open();
            stream.WriteByte(0x4D); // MZ header
            stream.WriteByte(0x5A);
        }

        var pipeline = NewPipelineForZip();
        var located = pipeline.ExtractZipAndLocateInstaller(zipPath, temp.Path, out var error);

        located.Should().NotBeNull();
        located!.Should().EndWith("Setup.exe");
        error.Should().BeEmpty();
    }

    [Fact]
    public void ExtractZipAndLocateInstaller_rejects_zip_slip_paths()
    {
        using var temp = new TempDir();
        var zipPath = Path.Combine(temp.Path, "evil.zip");
        using (var zip = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("../../escape.exe");
            using var stream = entry.Open();
            stream.WriteByte(0x4D);
        }

        var pipeline = NewPipelineForZip();
        var located = pipeline.ExtractZipAndLocateInstaller(zipPath, temp.Path, out var error);

        located.Should().BeNull();
        error.Should().Contain("escape");
    }

    [Fact]
    public void ExtractZipAndLocateInstaller_returns_error_when_no_installer_inside()
    {
        using var temp = new TempDir();
        var zipPath = Path.Combine(temp.Path, "docs.zip");
        using (var zip = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("ReadMe.txt");
            using var stream = entry.Open();
            stream.WriteByte(0x41);
        }

        var pipeline = NewPipelineForZip();
        var located = pipeline.ExtractZipAndLocateInstaller(zipPath, temp.Path, out var error);

        located.Should().BeNull();
        error.Should().Contain("did not contain");
    }

    private static InstallPipeline NewPipelineForZip() => new(
        new FakeRestorePointService(),
        new FakeBackupService(),
        new FakeWuApiClient(),
        NullLogger<InstallPipeline>.Instance);

    private static byte[] CreateDriverZip()
    {
        using var memory = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(
                   memory,
                   System.IO.Compression.ZipArchiveMode.Create,
                   leaveOpen: true))
        {
            var inf = archive.CreateEntry("Driver/realtek.inf");
            using (var writer = new StreamWriter(inf.Open()))
            {
                writer.Write("[Version]");
            }

            var setup = archive.CreateEntry("Setup.exe");
            using var setupStream = setup.Open();
            setupStream.Write([0x4D, 0x5A]);
        }
        return memory.ToArray();
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DriverUpdater.Tests", Guid.NewGuid().ToString("N"));
        public TempDir() { Directory.CreateDirectory(Path); }
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
        }
    }

    [Theory]
    [InlineData("vendor-installer:nvidia:610.47", "C:\\Temp\\nvidia.exe", "-s -noeula -noreboot")]
    [InlineData("vendor-installer:nullsoft:foo", "C:\\Temp\\setup.exe", "/S")]
    [InlineData("vendor-installer:amd-chipset:8.05.04.516", "C:\\Temp\\chipset.exe", "/S")]
    [InlineData("vendor-installer:inno:bar", "C:\\Temp\\bar.exe", "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART")]
    public void TryBuildVendorInstallerCommand_maps_known_prefixes_to_silent_args(string sourceUpdateId, string installerPath, string expectedArgs)
    {
        var candidate = new UpdateCandidate(
            ForHardwareId: "PCI\\X",
            Source: UpdateSource.Oem,
            NewVersion: new Version(1, 0, 0, 0),
            NewDate: new DateOnly(2026, 1, 1),
            DownloadUrl: new Uri("https://example.com/installer.exe"),
            SizeBytes: 0,
            KbArticle: null,
            IsSuperseded: false,
            SourceUpdateId: sourceUpdateId,
            SupersededIds: Array.Empty<string>(),
            InstallKind: UpdateInstallKind.VendorInstaller);

        var ok = InstallPipeline.TryBuildVendorInstallerCommand(candidate, installerPath, out var fileName, out var arguments, out var skipReason);

        ok.Should().BeTrue();
        fileName.Should().Be(installerPath);
        arguments.Should().Be(expectedArgs);
        skipReason.Should().BeEmpty();
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1641, true)]
    [InlineData(3010, true)]
    [InlineData(2, false)]
    [InlineData(1603, false)]
    public void IsSuccessfulInstallerExitCode_accepts_success_and_reboot_codes(int exitCode, bool expected)
    {
        InstallPipeline.IsSuccessfulInstallerExitCode(exitCode).Should().Be(expected);
    }

    [Theory]
    [InlineData("vendor-installer:amd-chipset:8.05", "Valid|CN=Advanced Micro Devices, Inc.", true)]
    [InlineData("vendor-installer:nullsoft:gigabyte:b850:lan", "Valid|CN=GIGA-BYTE TECHNOLOGY CO., LTD.", true)]
    [InlineData("vendor-installer:nvidia:610.62", "NotSigned|", false)]
    [InlineData("vendor-installer:amd-chipset:8.05", "Valid|CN=Unexpected Publisher", false)]
    public void TryValidateVendorSignature_requires_valid_expected_publisher(
        string sourceUpdateId,
        string signatureOutput,
        bool expected)
    {
        var valid = InstallPipeline.TryValidateVendorSignature(sourceUpdateId, signatureOutput, out var error);

        valid.Should().Be(expected);
        if (expected)
        {
            error.Should().BeEmpty();
        }
        else
        {
            error.Should().NotBeNullOrWhiteSpace();
        }
    }
}
