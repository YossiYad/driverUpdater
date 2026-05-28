using System.Runtime.CompilerServices;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using Microsoft.Extensions.Logging;

namespace DriverUpdater.Services.Sources;

public sealed class AmdChipsetSource : IUpdateSource
{
    public const string HttpClientName = "AmdChipset";
    internal const string ChipsetSupportUrl = "https://www.amd.com/en/support/chipsets";

    private static readonly TimeSpan AdvisoryAge = TimeSpan.FromDays(60);

    private readonly HttpClient _httpClient;
    private readonly TimeProvider _clock;
    private readonly ILogger<AmdChipsetSource> _logger;

    public AmdChipsetSource(HttpClient httpClient, ILogger<AmdChipsetSource> logger, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(logger);
        _httpClient = httpClient;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    public UpdateSource Kind => UpdateSource.Oem;

    public string DisplayName => "AMD Chipset";

    public async IAsyncEnumerable<UpdateCandidate> SearchAsync(
        IReadOnlyCollection<DriverInfo> drivers,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(drivers);
        await Task.Yield();

        var matched = drivers.Where(IsSupportedAmdChipsetDriver)
            .GroupBy(d => d.HardwareId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToArray();

        _logger.LogInformation("AMD Chipset source matched {Count} chipset/system drivers", matched.Length);
        if (matched.Length == 0)
        {
            yield break;
        }

        var now = _clock.GetUtcNow();
        var advisoryDate = DateOnly.FromDateTime(now.UtcDateTime.Date);
        var supportUri = new Uri(ChipsetSupportUrl);

        foreach (var driver in matched)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (driver.CurrentDate is { } currentDate
                && now - new DateTimeOffset(currentDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) < AdvisoryAge)
            {
                continue;
            }

            _logger.LogInformation("AMD Chipset: offering vendor page for {Device}", driver.DeviceName);
            yield return new UpdateCandidate(
                ForHardwareId: driver.HardwareId,
                Source: UpdateSource.Oem,
                NewVersion: new Version(advisoryDate.Year, advisoryDate.Month, advisoryDate.Day, 0),
                NewDate: advisoryDate,
                DownloadUrl: supportUri,
                SizeBytes: 0,
                KbArticle: null,
                IsSuperseded: false,
                SourceUpdateId: $"amd-chipset-page:{driver.HardwareId}",
                SupersededIds: Array.Empty<string>(),
                InstallKind: UpdateInstallKind.VendorPage,
                Confidence: UpdateConfidence.Advisory);
        }
    }

    internal static bool IsSupportedAmdChipsetDriver(DriverInfo driver)
    {
        if (driver.Category is not (DriverCategory.Chipset or DriverCategory.System))
        {
            return false;
        }

        if (Contains(driver.DeviceName, "Hyper-V") || Contains(driver.DeviceName, "Virtual"))
        {
            return false;
        }

        return Contains(driver.Provider, "Advanced Micro Devices")
            || Contains(driver.Manufacturer, "Advanced Micro Devices")
            || (Contains(driver.DeviceName, "AMD") && !Contains(driver.DeviceName, "Radeon"));
    }

    private static bool Contains(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
