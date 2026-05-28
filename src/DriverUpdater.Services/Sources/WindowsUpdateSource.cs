using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using Microsoft.Extensions.Logging;

namespace DriverUpdater.Services.Sources;

public sealed partial class WindowsUpdateSource : IUpdateSource
{
    private readonly IWuApiClient _client;
    private readonly ILogger<WindowsUpdateSource> _logger;

    public WindowsUpdateSource(IWuApiClient client, ILogger<WindowsUpdateSource> logger)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(logger);
        _client = client;
        _logger = logger;
    }

    public UpdateSource Kind => UpdateSource.WindowsUpdate;

    public string DisplayName => "Windows Update";

    public async IAsyncEnumerable<UpdateCandidate> SearchAsync(
        IReadOnlyCollection<DriverInfo> drivers,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(drivers);

        _logger.LogInformation("Windows Update search starting for {Count} drivers", drivers.Count);

        await foreach (var record in _client.SearchDriverUpdatesAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryMap(record, out var candidate))
            {
                yield return candidate;
            }
        }

        _logger.LogInformation("Windows Update search completed");
    }

    internal static bool TryMap(WuDriverUpdateRecord record, out UpdateCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (string.IsNullOrWhiteSpace(record.UpdateId))
        {
            candidate = null!;
            return false;
        }

        var version = ExtractVersionFromTitle(record.Title)
            ?? DateToVersion(record.DriverVerDate)
            ?? new Version(0, 0, 0, 0);

        var date = record.DriverVerDate ?? DateOnly.MinValue;
        var hardwareId = record.DriverHardwareId ?? string.Empty;
        var downloadUrl = !string.IsNullOrWhiteSpace(record.DownloadUrl)
            ? new Uri(record.DownloadUrl)
            : new Uri("about:blank");

        candidate = new UpdateCandidate(
            ForHardwareId: hardwareId,
            Source: UpdateSource.WindowsUpdate,
            NewVersion: version,
            NewDate: date,
            DownloadUrl: downloadUrl,
            SizeBytes: record.MaxDownloadSize,
            KbArticle: record.KbArticleIds.Count > 0 ? $"KB{record.KbArticleIds[0]}" : null,
            IsSuperseded: false,
            SourceUpdateId: record.UpdateId,
            SupersededIds: Array.Empty<string>());
        return true;
    }

    internal static Version? ExtractVersionFromTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var match = VersionPattern().Match(title);
        if (!match.Success)
        {
            return null;
        }

        return Version.TryParse(match.Value, out var parsed) ? parsed : null;
    }

    internal static Version? DateToVersion(DateOnly? date) =>
        date is { } d
            ? new Version(d.Year, d.Month, d.Day, 0)
            : null;

    [GeneratedRegex(@"\b\d+\.\d+(?:\.\d+){0,2}\b")]
    private static partial Regex VersionPattern();
}
