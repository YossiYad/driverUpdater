using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace DriverUpdater.App.Services;

public sealed class ExternalLinkOpener : IExternalLinkOpener
{
    private readonly ILogger<ExternalLinkOpener> _logger;

    public ExternalLinkOpener(ILogger<ExternalLinkOpener> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public bool Open(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps)
        {
            _logger.LogWarning("Blocked unsupported external link {ExternalLink}", uri);
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri)
            {
                UseShellExecute = true
            });
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not open external link {ExternalLink}", uri);
            return false;
        }
    }
}
