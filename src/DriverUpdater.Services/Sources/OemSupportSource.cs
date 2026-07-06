using System.Runtime.CompilerServices;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using Microsoft.Extensions.Logging;

namespace DriverUpdater.Services.Sources;

public sealed class OemSupportSource : IUpdateSource
{
    private static readonly TimeSpan AdvisoryAge = TimeSpan.FromDays(120);

    private readonly IOemDetectionService _oemDetectionService;
    private readonly TimeProvider _clock;
    private readonly ILogger<OemSupportSource> _logger;

    public OemSupportSource(
        IOemDetectionService oemDetectionService,
        ILogger<OemSupportSource> logger,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(oemDetectionService);
        ArgumentNullException.ThrowIfNull(logger);
        _oemDetectionService = oemDetectionService;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    public UpdateSource Kind => UpdateSource.Oem;

    public string DisplayName => "OEM motherboard support";

    public async IAsyncEnumerable<UpdateCandidate> SearchAsync(
        IReadOnlyCollection<DriverInfo> drivers,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(drivers);

        var oem = await _oemDetectionService.DetectAsync(cancellationToken).ConfigureAwait(false);
        if (oem is null || oem.Vendor == OemVendor.Unknown)
        {
            _logger.LogInformation(
                "OEM support source skipped: motherboard vendor could not be detected (oem={Oem})",
                oem is null ? "<null>" : oem.Vendor.ToString());
            yield break;
        }

        var now = _clock.GetUtcNow();
        var advisoryDate = DateOnly.FromDateTime(now.UtcDateTime.Date);
        foreach (var driver in drivers.Where(IsMotherboardDriverCandidate).GroupBy(d => d.HardwareId, StringComparer.OrdinalIgnoreCase).Select(g => g.First()))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (driver.CurrentDate is { } currentDate
                && now - new DateTimeOffset(currentDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) < AdvisoryAge)
            {
                _logger.LogDebug(
                    "OEM support check skipped for {Device}: installed driver dated {CurrentDate} is within the {Days}-day advisory window",
                    driver.DeviceName, currentDate, AdvisoryAge.TotalDays);
                continue;
            }

            _logger.LogInformation("Offering OEM support page for {Device} via {Vendor}", driver.DeviceName, oem.Vendor);
            yield return new UpdateCandidate(
                ForHardwareId: driver.HardwareId,
                Source: UpdateSource.Oem,
                NewVersion: new Version(advisoryDate.Year, advisoryDate.Month, advisoryDate.Day, 0),
                NewDate: advisoryDate,
                DownloadUrl: oem.FallbackUrl,
                SizeBytes: 0,
                KbArticle: null,
                IsSuperseded: false,
                SourceUpdateId: $"oem-support:{oem.Vendor}:{driver.HardwareId}",
                SupersededIds: Array.Empty<string>(),
                InstallKind: UpdateInstallKind.VendorPage,
                Confidence: UpdateConfidence.Advisory);
        }
    }

    internal static bool IsMotherboardDriverCandidate(DriverInfo driver)
    {
        if (driver.Category is DriverCategory.Display or DriverCategory.Printer or DriverCategory.Camera)
        {
            return false;
        }

        if (driver.Category is not (DriverCategory.Chipset
            or DriverCategory.System
            or DriverCategory.Storage
            or DriverCategory.Network
            or DriverCategory.Audio
            or DriverCategory.Bluetooth
            or DriverCategory.Usb
            or DriverCategory.Security))
        {
            return false;
        }

        if (Contains(driver.Provider, "Microsoft")
            || Contains(driver.Manufacturer, "Microsoft"))
        {
            return false;
        }

        if (HasDedicatedComponentVendor(driver))
        {
            return false;
        }

        if (Contains(driver.DeviceName, "Hyper-V")
            || Contains(driver.DeviceName, "Virtual")
            || Contains(driver.DeviceName, "ACPI")
            || Contains(driver.DeviceName, "Microsoft")
            || Contains(driver.DeviceName, "Generic ")
            || Contains(driver.DeviceName, "Composite ")
            || Contains(driver.DeviceName, "PCI standard")
            || Contains(driver.DeviceName, "PCI Express")
            || Contains(driver.DeviceName, "Motherboard resources")
            || Contains(driver.DeviceName, "System board")
            || Contains(driver.DeviceName, "System timer")
            || Contains(driver.DeviceName, "System speaker")
            || Contains(driver.DeviceName, "System CMOS")
            || Contains(driver.DeviceName, "High precision event timer")
            || Contains(driver.DeviceName, "Direct memory access controller")
            || Contains(driver.DeviceName, "Programmable interrupt controller")
            || Contains(driver.DeviceName, "Resource Hub")
            || Contains(driver.DeviceName, "UMBus")
            || Contains(driver.DeviceName, "Volume")
            || Contains(driver.DeviceName, "SQExt")
            || Contains(driver.DeviceName, "Nefarius"))
        {
            return false;
        }

        return true;
    }

    private static bool HasDedicatedComponentVendor(DriverInfo driver)
    {
        foreach (var keyword in DedicatedVendorKeywords)
        {
            if (Contains(driver.Provider, keyword)
                || Contains(driver.Manufacturer, keyword)
                || Contains(driver.DeviceName, keyword))
            {
                return true;
            }
        }
        return false;
    }

    private static readonly string[] DedicatedVendorKeywords =
    [
        "Advanced Micro Devices",
        "AMD",
        "NVIDIA",
        "Intel",
        "Realtek",
        "Logitech",
        "LIGHTSPEED",
        "Qualcomm",
        "MediaTek",
        "Broadcom"
    ];

    private static bool Contains(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
