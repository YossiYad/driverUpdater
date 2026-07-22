using System.Runtime.CompilerServices;
using System.Xml.Linq;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using Microsoft.Extensions.Logging;

namespace DriverUpdater.Services.Sources;

public sealed class OemToolUpdateSource : IUpdateSource
{
    private readonly IOemDetectionService _oemDetectionService;
    private readonly IVendorInstallerRunner? _toolRunner;
    private readonly IFileSignatureVerifier? _fileSignatureVerifier;
    private readonly TimeProvider _clock;
    private readonly ILogger<OemToolUpdateSource> _logger;

    public OemToolUpdateSource(
        IOemDetectionService oemDetectionService,
        ILogger<OemToolUpdateSource> logger,
        IVendorInstallerRunner? toolRunner = null,
        IFileSignatureVerifier? fileSignatureVerifier = null,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(oemDetectionService);
        ArgumentNullException.ThrowIfNull(logger);
        _oemDetectionService = oemDetectionService;
        _toolRunner = toolRunner;
        _fileSignatureVerifier = fileSignatureVerifier;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    public UpdateSource Kind => UpdateSource.Oem;

    public string DisplayName => "OEM automatic tool";

    public async IAsyncEnumerable<UpdateCandidate> SearchAsync(
        IReadOnlyCollection<DriverInfo> drivers,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(drivers);

        var oem = await _oemDetectionService.DetectAsync(cancellationToken).ConfigureAwait(false);
        if (oem is null)
        {
            _logger.LogInformation("OEM tool source skipped: OEM could not be detected");
            yield break;
        }
        if (!TryBuildToolCandidate(oem, out var toolId, out var toolUri))
        {
            _logger.LogInformation(
                "OEM tool source skipped: no supported vendor update tool (vendor={Vendor}, toolInstalled={ToolInstalled}, toolPath={ToolPath})",
                oem.Vendor, oem.ToolInstalled, oem.ToolPath ?? "<none>");
            yield break;
        }

        if (_toolRunner is null || _fileSignatureVerifier is null)
        {
            _logger.LogWarning("OEM tool source skipped: scan runner or signature verifier is not configured");
            yield break;
        }

        var signature = _fileSignatureVerifier.Verify(toolUri.LocalPath);
        if (!signature.IsTrusted || !IsExpectedToolPublisher(toolId, signature.Publisher))
        {
            _logger.LogWarning(
                "OEM tool source rejected {Tool}: trusted={Trusted}, publisher={Publisher}, reason={Reason}",
                toolUri.LocalPath,
                signature.IsTrusted,
                signature.Publisher ?? "<missing>",
                signature.ErrorMessage ?? "unexpected publisher");
            yield break;
        }

        var reportDirectory = Path.Combine(
            Path.GetTempPath(),
            "DriverUpdater",
            "OemScans",
            Guid.NewGuid().ToString("N"));
        if (!TryBuildScanCommand(toolId, reportDirectory, out var scanArguments, out var reportPath))
        {
            _logger.LogInformation("OEM tool source skipped: {Tool} has no non-interactive report mode", toolId);
            yield break;
        }

        int availableUpdates;
        try
        {
            Directory.CreateDirectory(reportDirectory);
            _logger.LogInformation("Running verified OEM update scan with {Tool}", oem.ToolName);
            var scan = await _toolRunner.RunAsync(toolUri.LocalPath, scanArguments, cancellationToken).ConfigureAwait(false);
            availableUpdates = CountApplicableUpdates(toolId, reportPath, reportDirectory);
            if (availableUpdates == 0)
            {
                _logger.LogInformation(
                    "OEM tool scan found no applicable driver or firmware updates (tool={Tool}, exit={ExitCode})",
                    toolId,
                    scan.ExitCode);
                yield break;
            }

            _logger.LogInformation(
                "OEM tool scan found {Count} applicable driver or firmware updates (tool={Tool}, exit={ExitCode})",
                availableUpdates,
                toolId,
                scan.ExitCode);
        }
        finally
        {
            TryDeleteReportDirectory(reportDirectory);
        }

        var now = _clock.GetUtcNow();
        var candidateDate = DateOnly.FromDateTime(now.UtcDateTime.Date);
        var sourceUpdateId = $"vendor-installer:oem-tool:{toolId}:{oem.Vendor}:{availableUpdates}";
        var driver = drivers
            .Where(IsOemToolDriverCandidate)
            .OrderByDescending(candidate => candidate.Category == DriverCategory.Firmware)
            .ThenByDescending(candidate => candidate.Category is DriverCategory.Chipset or DriverCategory.System)
            .FirstOrDefault();
        if (driver is null)
        {
            yield break;
        }

        cancellationToken.ThrowIfCancellationRequested();
        _logger.LogInformation(
            "Offering one OEM tool operation for {Count} applicable updates via {Tool}",
            availableUpdates,
            oem.ToolName);
        yield return new UpdateCandidate(
            ForHardwareId: driver.HardwareId,
            Source: UpdateSource.Oem,
            NewVersion: new Version(candidateDate.Year, candidateDate.Month, candidateDate.Day, availableUpdates),
            NewDate: candidateDate,
            DownloadUrl: toolUri,
            SizeBytes: 0,
            KbArticle: null,
            IsSuperseded: false,
            SourceUpdateId: sourceUpdateId,
            SupersededIds: Array.Empty<string>(),
            InstallKind: UpdateInstallKind.VendorInstaller,
            Confidence: UpdateConfidence.Confirmed);
    }

    internal static bool TryBuildToolCandidate(OemInfo oem, out string toolId, out Uri toolUri)
    {
        toolId = string.Empty;
        toolUri = null!;

        if (!oem.ToolInstalled || string.IsNullOrWhiteSpace(oem.ToolPath))
        {
            return false;
        }

        var fileName = Path.GetFileName(oem.ToolPath);
        toolId = oem.Vendor switch
        {
            OemVendor.Dell when fileName.Equals("dcu-cli.exe", StringComparison.OrdinalIgnoreCase) => "dell-command-update",
            OemVendor.Lenovo when fileName.Equals("tvsu.exe", StringComparison.OrdinalIgnoreCase) => "lenovo-system-update",
            OemVendor.Hp when fileName.Equals("HPImageAssistant.exe", StringComparison.OrdinalIgnoreCase) => "hp-image-assistant",
            _ => string.Empty
        };

        if (string.IsNullOrWhiteSpace(toolId))
        {
            return false;
        }

        toolUri = new Uri(oem.ToolPath);
        return true;
    }

    internal static bool IsOemToolDriverCandidate(DriverInfo driver)
    {
        if (driver.Category is DriverCategory.Display or DriverCategory.Printer or DriverCategory.Camera)
        {
            return false;
        }

        return driver.Category is DriverCategory.Chipset
            or DriverCategory.System
            or DriverCategory.Storage
            or DriverCategory.Network
            or DriverCategory.Audio
            or DriverCategory.Bluetooth
            or DriverCategory.Usb
            or DriverCategory.Security
            or DriverCategory.Firmware;
    }

    internal static bool TryBuildScanCommand(
        string toolId,
        string reportDirectory,
        out string arguments,
        out string reportPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reportDirectory);

        if (toolId.Equals("dell-command-update", StringComparison.OrdinalIgnoreCase))
        {
            reportPath = Path.Combine(reportDirectory, "UpdatesReport.xml");
            arguments = $"/scan -silent -updateType=driver,firmware -report=\"{reportPath}\"";
            return true;
        }

        if (toolId.Equals("hp-image-assistant", StringComparison.OrdinalIgnoreCase))
        {
            var reportBasePath = Path.Combine(reportDirectory, "HPIARecommendations");
            reportPath = reportBasePath + ".xml";
            arguments = $"/Operation:Analyze /Category:Drivers,Firmware /Selection:All /Action:List /Silent /Noninteractive /ReportFilePath:\"{reportBasePath}\"";
            return true;
        }

        arguments = string.Empty;
        reportPath = string.Empty;
        return false;
    }

    internal static int CountApplicableUpdates(string toolId, string reportPath, string reportDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reportPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(reportDirectory);

        try
        {
            if (!File.Exists(reportPath) && toolId.Equals("hp-image-assistant", StringComparison.OrdinalIgnoreCase))
            {
                reportPath = Directory.EnumerateFiles(reportDirectory, "*.xml", SearchOption.AllDirectories)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault() ?? reportPath;
            }
            if (!File.Exists(reportPath))
            {
                return 0;
            }

            var document = XDocument.Load(reportPath, LoadOptions.None);
            if (toolId.Equals("dell-command-update", StringComparison.OrdinalIgnoreCase))
            {
                return document.Descendants()
                    .Count(element => element.Name.LocalName.Equals("update", StringComparison.OrdinalIgnoreCase));
            }
            if (toolId.Equals("hp-image-assistant", StringComparison.OrdinalIgnoreCase))
            {
                return document.Descendants()
                    .Count(element => element.Name.LocalName.Equals("Recommendation", StringComparison.OrdinalIgnoreCase));
            }
        }
        catch (IOException)
        {
            return 0;
        }
        catch (System.Xml.XmlException)
        {
            return 0;
        }

        return 0;
    }

    internal static bool IsExpectedToolPublisher(string toolId, string? publisher)
    {
        if (string.IsNullOrWhiteSpace(publisher))
        {
            return false;
        }

        return toolId.ToLowerInvariant() switch
        {
            "dell-command-update" => publisher.Contains("Dell", StringComparison.OrdinalIgnoreCase),
            "lenovo-system-update" => publisher.Contains("Lenovo", StringComparison.OrdinalIgnoreCase),
            "hp-image-assistant" => publisher.Contains("HP Inc", StringComparison.OrdinalIgnoreCase)
                || publisher.Contains("Hewlett-Packard", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private static void TryDeleteReportDirectory(string reportDirectory)
    {
        try
        {
            if (Directory.Exists(reportDirectory))
            {
                Directory.Delete(reportDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Scan reports are temporary and can be cleaned up by the next system temp cleanup.
        }
        catch (UnauthorizedAccessException)
        {
            // Scan reports are temporary and can be cleaned up by the next system temp cleanup.
        }
    }
}
