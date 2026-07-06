using System.Runtime.CompilerServices;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using Microsoft.Extensions.Logging;

namespace DriverUpdater.Services.Sources;

public sealed class OemToolUpdateSource : IUpdateSource
{
    private static readonly TimeSpan CandidateAge = TimeSpan.FromDays(120);

    private readonly IOemDetectionService _oemDetectionService;
    private readonly TimeProvider _clock;
    private readonly ILogger<OemToolUpdateSource> _logger;

    public OemToolUpdateSource(
        IOemDetectionService oemDetectionService,
        ILogger<OemToolUpdateSource> logger,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(oemDetectionService);
        ArgumentNullException.ThrowIfNull(logger);
        _oemDetectionService = oemDetectionService;
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

        var now = _clock.GetUtcNow();
        var candidateDate = DateOnly.FromDateTime(now.UtcDateTime.Date);
        var sourceUpdateId = $"vendor-installer:oem-tool:{toolId}:{oem.Vendor}";

        foreach (var driver in drivers.Where(IsOemToolDriverCandidate))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (driver.CurrentDate is { } currentDate
                && now - new DateTimeOffset(currentDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) < CandidateAge)
            {
                _logger.LogDebug(
                    "OEM tool check skipped for {Device}: installed driver dated {CurrentDate} is within the {Days}-day window",
                    driver.DeviceName, currentDate, CandidateAge.TotalDays);
                continue;
            }

            _logger.LogInformation("Offering automatic OEM tool run for {Device} via {Tool}", driver.DeviceName, oem.ToolName);
            yield return new UpdateCandidate(
                ForHardwareId: driver.HardwareId,
                Source: UpdateSource.Oem,
                NewVersion: new Version(candidateDate.Year, candidateDate.Month, candidateDate.Day, 0),
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
}
