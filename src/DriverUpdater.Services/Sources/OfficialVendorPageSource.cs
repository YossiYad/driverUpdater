using System.Runtime.CompilerServices;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using Microsoft.Extensions.Logging;

namespace DriverUpdater.Services.Sources;

public sealed class OfficialVendorPageSource : IUpdateSource
{
    private static readonly TimeSpan DefaultAdvisoryAge = TimeSpan.FromDays(180);
    private static readonly TimeSpan DisplayAdvisoryAge = TimeSpan.FromDays(14);

    private readonly TimeProvider _clock;
    private readonly ILogger<OfficialVendorPageSource> _logger;

    public OfficialVendorPageSource(ILogger<OfficialVendorPageSource> logger, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    public UpdateSource Kind => UpdateSource.Oem;

    public string DisplayName => "Official vendor support";

    public async IAsyncEnumerable<UpdateCandidate> SearchAsync(
        IReadOnlyCollection<DriverInfo> drivers,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(drivers);

        var now = _clock.GetUtcNow();
        foreach (var driver in drivers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();

            if (!TryResolveVendorPage(driver, out var vendorName, out var page))
            {
                continue;
            }

            var advisoryAge = driver.Category == DriverCategory.Display ? DisplayAdvisoryAge : DefaultAdvisoryAge;
            if (driver.CurrentDate is { } currentDate
                && now - new DateTimeOffset(currentDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) < advisoryAge)
            {
                continue;
            }

            _logger.LogInformation("Offering official vendor check page for {Device}", driver.DeviceName);
            var advisoryDate = DateOnly.FromDateTime(now.UtcDateTime.Date);
            yield return new UpdateCandidate(
                ForHardwareId: driver.HardwareId,
                Source: UpdateSource.Oem,
                NewVersion: new Version(advisoryDate.Year, advisoryDate.Month, advisoryDate.Day, 0),
                NewDate: advisoryDate,
                DownloadUrl: page,
                SizeBytes: 0,
                KbArticle: null,
                IsSuperseded: false,
                SourceUpdateId: $"vendor-page:{vendorName}:{driver.HardwareId}",
                SupersededIds: Array.Empty<string>(),
                InstallKind: UpdateInstallKind.VendorPage,
                Confidence: UpdateConfidence.Advisory);
        }
    }

    internal static bool TryResolveVendorPage(DriverInfo driver, out string vendorName, out Uri page)
    {
        if (IsNvidiaDisplay(driver))
        {
            vendorName = "NVIDIA";
            page = new Uri("https://www.nvidia.com/Download/index.aspx");
            return true;
        }

        if (IsAmdDisplay(driver))
        {
            vendorName = "AMD";
            page = new Uri("https://www.amd.com/en/support/download/drivers.html");
            return true;
        }

        if (IsIntelDisplayOrNetwork(driver))
        {
            vendorName = "Intel";
            page = new Uri("https://www.intel.com/content/www/us/en/download-center/home.html");
            return true;
        }

        if (IsRealtekAudioOrNetwork(driver))
        {
            vendorName = "Realtek";
            page = new Uri("https://www.realtek.com/Download/List");
            return true;
        }

        if (IsLogitechUsbOrInput(driver))
        {
            vendorName = "Logitech";
            page = new Uri("https://support.logi.com/hc/en-us/downloads");
            return true;
        }

        vendorName = string.Empty;
        page = null!;
        return false;
    }

    private static bool IsNvidiaDisplay(DriverInfo driver) =>
        driver.Category == DriverCategory.Display
        && (Contains(driver.Provider, "NVIDIA") || Contains(driver.Manufacturer, "NVIDIA") || Contains(driver.DeviceName, "NVIDIA")
            || Contains(driver.DeviceName, "GeForce") || Contains(driver.DeviceName, "Quadro") || Contains(driver.DeviceName, "RTX") || Contains(driver.DeviceName, "GTX"));

    private static bool IsAmdDisplay(DriverInfo driver) =>
        driver.Category == DriverCategory.Display
        && (Contains(driver.Provider, "Advanced Micro Devices") || Contains(driver.Manufacturer, "Advanced Micro Devices")
            || Contains(driver.Provider, "AMD") || Contains(driver.Manufacturer, "AMD")
            || Contains(driver.DeviceName, "Radeon") || Contains(driver.DeviceName, "AMD"));

    private static bool IsIntelDisplayOrNetwork(DriverInfo driver) =>
        driver.Category is DriverCategory.Display or DriverCategory.Network or DriverCategory.Bluetooth
        && (Contains(driver.Provider, "Intel") || Contains(driver.Manufacturer, "Intel") || Contains(driver.DeviceName, "Intel")
            || (driver.Category == DriverCategory.Display && (Contains(driver.DeviceName, "Iris") || Contains(driver.DeviceName, "Arc") || Contains(driver.DeviceName, "UHD Graphics") || Contains(driver.DeviceName, "HD Graphics"))));

    private static bool IsRealtekAudioOrNetwork(DriverInfo driver) =>
        driver.Category is DriverCategory.Audio or DriverCategory.Network or DriverCategory.Bluetooth
        && (Contains(driver.Provider, "Realtek") || Contains(driver.Manufacturer, "Realtek") || Contains(driver.DeviceName, "Realtek"));

    private static bool IsLogitechUsbOrInput(DriverInfo driver) =>
        driver.Category is DriverCategory.Usb or DriverCategory.Input or DriverCategory.HumanInterface
        && (Contains(driver.Provider, "Logitech") || Contains(driver.Manufacturer, "Logitech") || Contains(driver.DeviceName, "Logitech") || Contains(driver.DeviceName, "LIGHTSPEED"));

    private static bool Contains(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
