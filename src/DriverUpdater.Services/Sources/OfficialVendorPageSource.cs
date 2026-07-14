using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using Microsoft.Extensions.Logging;

namespace DriverUpdater.Services.Sources;

public sealed partial class OfficialVendorPageSource : IUpdateSource
{
    public const string HttpClientName = "OfficialVendorPages";

    private static readonly TimeSpan DefaultAdvisoryAge = TimeSpan.FromDays(180);
    private static readonly TimeSpan DisplayAdvisoryAge = TimeSpan.FromDays(14);

    private readonly TimeProvider _clock;
    private readonly Func<string, bool> _fileExists;
    private readonly HttpClient? _httpClient;
    private readonly ILogger<OfficialVendorPageSource> _logger;
    private readonly Lazy<bool> _gHubInstalled;

    public OfficialVendorPageSource(
        ILogger<OfficialVendorPageSource> logger,
        TimeProvider? clock = null,
        Func<string, bool>? fileExists = null,
        HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        _fileExists = fileExists ?? File.Exists;
        _httpClient = httpClient;
        _gHubInstalled = new Lazy<bool>(DetectGHub);
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
                _logger.LogDebug(
                    "Vendor page check skipped for {Device}: no vendor page mapping (provider={Provider}, category={Category})",
                    driver.DeviceName, driver.Provider, driver.Category);
                continue;
            }

            if (vendorName == "Logitech" && _gHubInstalled.Value)
            {
                _logger.LogInformation("Skipping Logitech driver {Device} because Logitech G Hub is installed and handles updates", driver.DeviceName);
                continue;
            }

            var advisoryAge = driver.Category == DriverCategory.Display ? DisplayAdvisoryAge : DefaultAdvisoryAge;
            if (driver.CurrentDate is { } currentDate
                && now - new DateTimeOffset(currentDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) < advisoryAge)
            {
                _logger.LogDebug(
                    "Vendor page check skipped for {Device}: installed driver dated {CurrentDate} is within the {Days}-day advisory window",
                    driver.DeviceName, currentDate, advisoryAge.TotalDays);
                continue;
            }

            _logger.LogInformation("Offering official vendor check page for {Device}", driver.DeviceName);
            var advisoryDate = DateOnly.FromDateTime(now.UtcDateTime.Date);
            if (await TryBuildInstallerCandidateAsync(driver, vendorName, page, advisoryDate, cancellationToken).ConfigureAwait(false) is { } installer)
            {
                yield return installer;
                continue;
            }

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

    private async Task<UpdateCandidate?> TryBuildInstallerCandidateAsync(
        DriverInfo driver,
        string vendorName,
        Uri page,
        DateOnly candidateDate,
        CancellationToken cancellationToken)
    {
        if (_httpClient is null)
        {
            return null;
        }

        try
        {
            var html = await _httpClient.GetStringAsync(page, cancellationToken).ConfigureAwait(false);
            if (!TryFindAppInstallablePackage(page, html, out var packageUrl, out var installerKind))
            {
                _logger.LogInformation(
                    "No direct .msi/.zip installer found on {Page} for {Device} ({Length} bytes scanned); offering the page itself",
                    page, driver.DeviceName, html.Length);
                return null;
            }

            _logger.LogInformation(
                "Resolved official vendor installer for {Device}: {Url}",
                driver.DeviceName,
                packageUrl);
            return new UpdateCandidate(
                ForHardwareId: driver.HardwareId,
                Source: UpdateSource.Oem,
                NewVersion: new Version(candidateDate.Year, candidateDate.Month, candidateDate.Day, 0),
                NewDate: candidateDate,
                DownloadUrl: packageUrl,
                SizeBytes: 0,
                KbArticle: null,
                IsSuperseded: false,
                SourceUpdateId: $"vendor-installer:{installerKind}:{vendorName}:{driver.HardwareId}",
                SupersededIds: Array.Empty<string>(),
                InstallKind: UpdateInstallKind.VendorInstaller,
                Confidence: UpdateConfidence.Confirmed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not resolve a direct vendor installer from {Page}", page);
            return null;
        }
    }

    internal static bool TryFindAppInstallablePackage(
        Uri page,
        string html,
        out Uri packageUrl,
        out string installerKind)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(html);

        foreach (Match match in HrefPattern().Matches(html))
        {
            var raw = match.Groups["url"].Value;
            if (!Uri.TryCreate(page, raw, out var resolved)
                || resolved.Scheme is not ("http" or "https"))
            {
                continue;
            }

            var extension = Path.GetExtension(resolved.LocalPath);
            if (extension.Equals(".msi", StringComparison.OrdinalIgnoreCase))
            {
                packageUrl = resolved;
                installerKind = "msi-wrapper";
                return true;
            }

            if (extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                packageUrl = resolved;
                installerKind = "zip-inf";
                return true;
            }
        }

        packageUrl = null!;
        installerKind = string.Empty;
        return false;
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

    private bool DetectGHub()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        var candidates = new[]
        {
            Path.Combine(programFiles, "LGHUB", "lghub.exe"),
            Path.Combine(programFilesX86, "LGHUB", "lghub.exe"),
            Path.Combine(localAppData, "LGHUB", "lghub.exe"),
            Path.Combine(programFiles, "Logitech", "LogiOptionsPlus", "logioptionsplus.exe"),
            Path.Combine(programFilesX86, "Logitech", "LogiOptionsPlus", "logioptionsplus.exe")
        };

        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrEmpty(candidate) && _fileExists(candidate))
            {
                _logger.LogInformation("Detected Logitech G Hub / Options+ at {Path}", candidate);
                return true;
            }
        }

        return false;
    }

    [GeneratedRegex(@"href\s*=\s*[""'](?<url>[^""'#?]+\.(?:msi|zip)(?:\?[^""']*)?)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex HrefPattern();
}
