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

        var psi = new ProcessStartInfo
        {
            FileName = candidate.DownloadUrl.AbsoluteUri,
            UseShellExecute = true
        };
        Process.Start(psi);
        _logger.LogInformation("Opened update page {Url}", candidate.DownloadUrl);
    }
}
