using System.Diagnostics;
using DriverUpdater.Core.Models;
using Microsoft.Extensions.Logging;

namespace DriverUpdater.App.Services;

public sealed class UpdatePageOpener : IUpdatePageOpener
{
    private readonly ILogger<UpdatePageOpener> _logger;

    public UpdatePageOpener(ILogger<UpdatePageOpener> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public void Open(UpdateCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (!candidate.DownloadUrl.IsAbsoluteUri
            || !string.Equals(candidate.DownloadUrl.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(candidate.DownloadUrl.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Only HTTP and HTTPS update pages can be opened.");
        }

        var psi = new ProcessStartInfo
        {
            FileName = candidate.DownloadUrl.AbsoluteUri,
            UseShellExecute = true
        };
        Process.Start(psi);
        _logger.LogInformation("Opened update page {Url}", candidate.DownloadUrl);
    }
}
