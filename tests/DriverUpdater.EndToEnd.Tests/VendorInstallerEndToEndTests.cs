using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using DriverUpdater.Core.Options;
using DriverUpdater.EndToEnd.Tests.Harness;
using DriverUpdater.Services.Backup;
using DriverUpdater.Services.Install;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriverUpdater.EndToEnd.Tests;

/// <summary>
/// Covers the vendor-installer route end to end: the real
/// <see cref="VendorPageInstallerResolver"/> reads a vendor support page over HTTP, picks a
/// directly installable package, and the real <see cref="InstallPipeline"/> downloads it,
/// validates it, and runs it with the vendor's documented silent flags. Includes the safety
/// checks that stop a hostile or broken download from being executed.
/// </summary>
public sealed class VendorInstallerEndToEndTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    private static readonly DriverInfo ChipsetDevice = new(
        DeviceId: @"PCI\VEN_1022&DEV_1630\3&11583659&0&00",
        HardwareId: @"PCI\VEN_1022&DEV_1630",
        DeviceName: "AMD PCI Device",
        Category: DriverCategory.Chipset,
        Provider: "Advanced Micro Devices, Inc.",
        Manufacturer: "Advanced Micro Devices, Inc.",
        CurrentVersion: new Version(1, 0, 0, 100),
        CurrentDate: new DateOnly(2022, 1, 1),
        InfName: "oem5.inf",
        InfPath: null,
        IsSigned: true,
        DeviceClass: "SYSTEM");

    private static UpdateCandidate VendorPageCandidate(string pageUrl) => new(
        ForHardwareId: ChipsetDevice.HardwareId,
        Source: UpdateSource.Oem,
        NewVersion: new Version(7, 6, 2, 123),
        NewDate: new DateOnly(2024, 8, 1),
        DownloadUrl: new Uri(pageUrl),
        SizeBytes: 0,
        KbArticle: null,
        IsSuperseded: false,
        SourceUpdateId: "amd-chipset-page:7.06.02.123",
        SupersededIds: Array.Empty<string>(),
        InstallKind: UpdateInstallKind.VendorPage,
        Confidence: UpdateConfidence.Advisory);

    private static byte[] PortableExecutable()
    {
        var bytes = new byte[512];
        bytes[0] = 0x4D; // 'M'
        bytes[1] = 0x5A; // 'Z'
        return bytes;
    }

    private InstallPipeline BuildPipeline(
        IHttpClientFactory downloads,
        IVendorInstallerRunner runner,
        IVendorPageInstallerResolver? resolver,
        IInstalledDriverProbe? probe = null)
    {
        var pnputil = new FakePnPUtilRunner();
        return new InstallPipeline(
            new FakeRestorePointService(),
            new BackupService(
                pnputil,
                new StaticOptionsMonitor<BackupSettings>(new BackupSettings { RootPath = _workspace.Path("Backups") }),
                NullLogger<BackupService>.Instance),
            new FakeWuApiClient(),
            NullLogger<InstallPipeline>.Instance,
            pnputil: pnputil,
            powerShell: new FakeExpandPowerShellInvoker(),
            vendorInstallerRunner: runner,
            httpClientFactory: downloads,
            vendorPageResolver: resolver,
            installedDriverProbe: probe,
            fileSignatureVerifier: new FakeFileSignatureVerifier(ChipsetDevice.Manufacturer));
    }

    /// <summary>Always reports a trusted signature from the given publisher.</summary>
    private sealed class FakeFileSignatureVerifier : IFileSignatureVerifier
    {
        private readonly FileSignatureVerification _result;

        public FakeFileSignatureVerifier(string publisher) => _result = new FileSignatureVerification(true, publisher, "TEST", null);

        public FileSignatureVerification Verify(string filePath) => _result;
    }

    private static VendorPageInstallerResolver BuildResolver(RoutingHttpClientFactory http) =>
        new(http, NullLogger<VendorPageInstallerResolver>.Instance);

    [Fact]
    public async Task A_vendor_page_offering_a_known_installer_is_downloaded_and_run_silently()
    {
        const string pageUrl = "https://www.amd.com/en/support/downloads/drivers.html/chipsets/am5/x670.html";
        const string installerUrl = "https://drivers.amd.com/drivers/amd_chipset_software_7.06.02.123.exe";
        var http = new RoutingHttpClientFactory()
            .Serve(pageUrl, $"<html><body><a href=\"{installerUrl}\">Download</a></body></html>")
            .Serve(installerUrl, PortableExecutable());

        var runner = new FakeVendorInstallerRunner();
        var probe = new ScriptedInstalledDriverProbe()
            .Always(ChipsetDevice.DeviceId, new InstalledDriverState(new Version(1, 0, 0, 200), new DateOnly(2024, 8, 1)));
        var pipeline = BuildPipeline(http, runner, BuildResolver(http), probe);

        var finished = await pipeline.ExecuteAsync(
            UpdateOperation.NewPending(VendorPageCandidate(pageUrl), ChipsetDevice),
            new InstallOptions(CreateRestorePoint: false, BackupCurrentDriver: false, DryRun: false));

        finished.Status.Should().Be(UpdateStatus.Succeeded);
        finished.Candidate.InstallKind.Should().Be(UpdateInstallKind.VendorInstaller);
        runner.Invocations.Should().ContainSingle();
        runner.Invocations[0].FileName.Should().EndWith("amd_chipset_software_7.06.02.123.exe");
        runner.Invocations[0].Arguments.Should().Be("-INSTALL", "AMD documents -INSTALL as the unattended flag");
        http.RequestedUris.Should().Contain(u => u.AbsoluteUri == installerUrl);
    }

    [Fact]
    public async Task A_vendor_page_with_only_unrecognised_installers_is_skipped_without_launching_it()
    {
        const string pageUrl = "https://www.example-vendor.invalid/support/audio";
        const string mysteryUrl = "https://cdn.example-vendor.invalid/mystery-setup.exe";
        var http = new RoutingHttpClientFactory()
            .Serve(pageUrl, $"<html><body><a href=\"{mysteryUrl}\">Get it</a></body></html>")
            .Serve(mysteryUrl, PortableExecutable());

        var runner = new FakeVendorInstallerRunner();
        var pipeline = BuildPipeline(http, runner, BuildResolver(http));

        var finished = await pipeline.ExecuteAsync(
            UpdateOperation.NewPending(VendorPageCandidate(pageUrl), ChipsetDevice),
            new InstallOptions(CreateRestorePoint: false, BackupCurrentDriver: false, DryRun: false));

        finished.Status.Should().Be(UpdateStatus.Skipped);
        runner.Invocations.Should().BeEmpty("an .exe with unknown silent flags must never be launched unattended");
    }

    [Fact]
    public async Task A_vendor_page_row_costs_no_restore_point_and_no_driver_backup_when_it_cannot_be_installed()
    {
        const string pageUrl = "https://www.example-vendor.invalid/support/gpu";
        var http = new RoutingHttpClientFactory().Serve(pageUrl, "<html><body>No downloads here.</body></html>");

        var restorePoints = new FakeRestorePointService();
        var pnputil = new FakePnPUtilRunner();
        var pipeline = new InstallPipeline(
            restorePoints,
            new BackupService(
                pnputil,
                new StaticOptionsMonitor<BackupSettings>(new BackupSettings { RootPath = _workspace.Path("Backups") }),
                NullLogger<BackupService>.Instance),
            new FakeWuApiClient(),
            NullLogger<InstallPipeline>.Instance,
            pnputil: pnputil,
            vendorInstallerRunner: new FakeVendorInstallerRunner(),
            httpClientFactory: http,
            vendorPageResolver: BuildResolver(http));

        var finished = await pipeline.ExecuteAsync(
            UpdateOperation.NewPending(VendorPageCandidate(pageUrl), ChipsetDevice),
            new InstallOptions(CreateRestorePoint: true, BackupCurrentDriver: true, DryRun: false));

        finished.Status.Should().Be(UpdateStatus.Skipped);
        restorePoints.Descriptions.Should().BeEmpty("a GPU driver export alone can exceed 1 GB");
        pnputil.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task A_cdn_that_serves_an_html_error_page_instead_of_the_installer_is_refused()
    {
        const string installerUrl = "https://drivers.amd.com/drivers/amd_chipset_software_7.06.02.123.exe";
        var http = new RoutingHttpClientFactory()
            .Serve(installerUrl, "<html><body>Download-Incomplete</body></html>");

        var runner = new FakeVendorInstallerRunner();
        var pipeline = BuildPipeline(http, runner, resolver: null);

        var candidate = VendorPageCandidate("https://unused.invalid") with
        {
            DownloadUrl = new Uri(installerUrl),
            InstallKind = UpdateInstallKind.VendorInstaller,
            SourceUpdateId = "vendor-installer:amd-chipset:7.06.02.123"
        };

        var finished = await pipeline.ExecuteAsync(
            UpdateOperation.NewPending(candidate, ChipsetDevice),
            new InstallOptions(CreateRestorePoint: false, BackupCurrentDriver: false, DryRun: false));

        finished.Status.Should().Be(UpdateStatus.Failed);
        finished.ErrorMessage.Should().Contain("not a valid Windows executable");
        runner.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task A_zip_that_tries_to_escape_the_extraction_folder_is_rejected()
    {
        var zipPath = _workspace.Path("evil.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("../../escaped.exe");
            using var stream = entry.Open();
            stream.Write(PortableExecutable());
        }

        var pipeline = BuildPipeline(
            new RoutingHttpClientFactory(),
            new FakeVendorInstallerRunner(),
            resolver: null);
        var workDir = _workspace.Path("work");

        var located = pipeline.ExtractZipAndLocateInstaller(zipPath, workDir, out _, out var error);

        located.Should().BeNull();
        error.Should().Contain("escapes extraction directory");
        File.Exists(_workspace.Path("escaped.exe")).Should().BeFalse();
        Directory.Exists(Path.Combine(workDir, "extracted")).Should().BeTrue();
        Directory.GetFiles(Path.Combine(workDir, "extracted"), "*", SearchOption.AllDirectories)
            .Should().BeEmpty("nothing from a zip-slip archive may be written to disk");
    }

    [Fact]
    public void A_zip_that_contains_a_setup_program_is_extracted_and_the_installer_is_located()
    {
        var zipPath = _workspace.Path("driver.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            foreach (var name in new[] { "readme.txt", "bin/deep/Helper.exe", "Setup.exe" })
            {
                var entry = archive.CreateEntry(name);
                using var stream = entry.Open();
                stream.Write(PortableExecutable());
            }
        }

        var pipeline = BuildPipeline(
            new RoutingHttpClientFactory(),
            new FakeVendorInstallerRunner(),
            resolver: null);

        var located = pipeline.ExtractZipAndLocateInstaller(zipPath, _workspace.Path("work2"), out _, out var error);

        error.Should().BeEmpty();
        located.Should().NotBeNull();
        Path.GetFileName(located!).Should().Be("Setup.exe", "the shallowest recognised installer wins");
    }

    [Fact]
    public async Task An_installer_that_fails_is_reported_with_its_exit_code_and_output()
    {
        const string installerUrl = "https://drivers.amd.com/drivers/amd_chipset_software_7.06.02.123.exe";
        var http = new RoutingHttpClientFactory().Serve(installerUrl, PortableExecutable());
        var runner = new FakeVendorInstallerRunner((_, _) =>
            new ProcessResult(2, string.Empty, "Setup could not continue."));
        var pipeline = BuildPipeline(http, runner, resolver: null);

        var candidate = VendorPageCandidate("https://unused.invalid") with
        {
            DownloadUrl = new Uri(installerUrl),
            InstallKind = UpdateInstallKind.VendorInstaller,
            SourceUpdateId = "vendor-installer:amd-chipset:7.06.02.123"
        };

        var finished = await pipeline.ExecuteAsync(
            UpdateOperation.NewPending(candidate, ChipsetDevice),
            new InstallOptions(CreateRestorePoint: false, BackupCurrentDriver: false, DryRun: false));

        finished.Status.Should().Be(UpdateStatus.Failed);
        finished.ErrorMessage.Should().Contain("exit 2");
        finished.ErrorMessage.Should().Contain("Setup could not continue.");
    }

    [Fact]
    public async Task The_temporary_download_folder_is_always_cleaned_up()
    {
        const string installerUrl = "https://drivers.amd.com/drivers/amd_chipset_software_7.06.02.123.exe";
        var http = new RoutingHttpClientFactory().Serve(installerUrl, PortableExecutable());
        var pipeline = BuildPipeline(http, new FakeVendorInstallerRunner(), resolver: null);

        var operation = UpdateOperation.NewPending(
            VendorPageCandidate("https://unused.invalid") with
            {
                DownloadUrl = new Uri(installerUrl),
                InstallKind = UpdateInstallKind.VendorInstaller,
                SourceUpdateId = "vendor-installer:amd-chipset:7.06.02.123"
            },
            ChipsetDevice);

        await pipeline.ExecuteAsync(
            operation,
            new InstallOptions(CreateRestorePoint: false, BackupCurrentDriver: false, DryRun: false));

        var workDir = Path.Combine(Path.GetTempPath(), "DriverUpdater", operation.OperationId.ToString("N"));
        Directory.Exists(workDir).Should().BeFalse();
    }

    /// <summary>Serves per-URL payloads so page fetch and package download can differ.</summary>
    private sealed class RoutingHttpClientFactory : IHttpClientFactory
    {
        private readonly Dictionary<string, byte[]> _payloads = new(StringComparer.OrdinalIgnoreCase);

        public List<Uri> RequestedUris { get; } = new();

        public RoutingHttpClientFactory Serve(string url, string body) =>
            Serve(url, System.Text.Encoding.UTF8.GetBytes(body));

        public RoutingHttpClientFactory Serve(string url, byte[] body)
        {
            _payloads[url] = body;
            return this;
        }

        public HttpClient CreateClient(string name) => new(new Handler(this), disposeHandler: false);

        private sealed class Handler : HttpMessageHandler
        {
            private readonly RoutingHttpClientFactory _owner;

            public Handler(RoutingHttpClientFactory owner) => _owner = owner;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var uri = request.RequestUri!;
                lock (_owner.RequestedUris)
                {
                    _owner.RequestedUris.Add(uri);
                }

                if (!_owner._payloads.TryGetValue(uri.AbsoluteUri, out var payload))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                    {
                        Content = new ByteArrayContent(Array.Empty<byte>())
                    });
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(payload)
                });
            }
        }
    }
}
