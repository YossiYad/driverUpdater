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

        if (driver.Provider.Equals("Microsoft", StringComparison.OrdinalIgnoreCase)
            && driver.Category is not (DriverCategory.Chipset or DriverCategory.System or DriverCategory.Storage or DriverCategory.Network or DriverCategory.Audio or DriverCategory.Bluetooth))
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
            or DriverCategory.Security;
    }
}
