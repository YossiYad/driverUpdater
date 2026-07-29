using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace DriverUpdater.Infrastructure.VendorInstallers;

internal sealed class AmdInstallerContinuationWaiter
{
    private const string ContinuationProcessName = "AMDSoftwareInstaller";
    private static readonly TimeSpan ProductionPollInterval = TimeSpan.FromMilliseconds(500);
    private const int ProductionDiscoveryPolls = 20;
    private const int ProductionQuietPolls = 4;
    private const int ProductionCompletionPolls = 5400;

    private readonly ILogger _logger;
    private readonly Func<IReadOnlySet<int>> _processIds;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeSpan _pollInterval;
    private readonly int _discoveryPolls;
    private readonly int _quietPolls;
    private readonly int _completionPolls;

    internal AmdInstallerContinuationWaiter(ILogger logger)
        : this(
            logger,
            GetContinuationProcessIds,
            static (delay, cancellationToken) => Task.Delay(delay, cancellationToken),
            ProductionPollInterval,
            ProductionDiscoveryPolls,
            ProductionQuietPolls,
            ProductionCompletionPolls)
    {
    }

    internal AmdInstallerContinuationWaiter(
        ILogger logger,
        Func<IReadOnlySet<int>> processIds,
        Func<TimeSpan, CancellationToken, Task> delay,
        TimeSpan pollInterval,
        int discoveryPolls,
        int quietPolls,
        int completionPolls)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(processIds);
        ArgumentNullException.ThrowIfNull(delay);
        if (pollInterval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(pollInterval));
        }
        ArgumentOutOfRangeException.ThrowIfLessThan(discoveryPolls, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(quietPolls, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(completionPolls, 1);

        _logger = logger;
        _processIds = processIds;
        _delay = delay;
        _pollInterval = pollInterval;
        _discoveryPolls = discoveryPolls;
        _quietPolls = quietPolls;
        _completionPolls = completionPolls;
    }

    internal bool ShouldMonitor(string installerPath)
    {
        var fileName = Path.GetFileName(installerPath);
        return fileName.Contains("amd-software-adrenalin", StringComparison.OrdinalIgnoreCase);
    }

    internal IReadOnlySet<int> CaptureExistingProcesses(string installerPath) =>
        ShouldMonitor(installerPath)
            ? SafeGetProcessIds()
            : new HashSet<int>();

    internal async Task WaitForCompletionAsync(
        string installerPath,
        IReadOnlySet<int> existingProcessIds,
        CancellationToken cancellationToken)
    {
        if (!ShouldMonitor(installerPath))
        {
            return;
        }

        var sawContinuation = false;
        var discoveryPollsRemaining = _discoveryPolls;
        var completionPollsRemaining = _completionPolls;
        var quietPolls = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var activeIds = SafeGetProcessIds()
                .Where(processId => !existingProcessIds.Contains(processId))
                .ToArray();

            if (activeIds.Length > 0)
            {
                if (!sawContinuation)
                {
                    _logger.LogInformation(
                        "AMD launcher exited, but {ProcessName} is still installing in process(es) {ProcessIds}; waiting before driver verification",
                        ContinuationProcessName,
                        string.Join(", ", activeIds));
                }

                sawContinuation = true;
                quietPolls = 0;
                completionPollsRemaining--;
                if (completionPollsRemaining <= 0)
                {
                    throw new TimeoutException(
                        $"AMD installer continuation did not finish within {_pollInterval * _completionPolls}.");
                }
            }
            else if (!sawContinuation)
            {
                discoveryPollsRemaining--;
                if (discoveryPollsRemaining <= 0)
                {
                    _logger.LogDebug(
                        "AMD launcher exited and no {ProcessName} continuation appeared during the discovery window",
                        ContinuationProcessName);
                    return;
                }
            }
            else
            {
                quietPolls++;
                if (quietPolls >= _quietPolls)
                {
                    _logger.LogInformation(
                        "AMD installer continuation finished; driver verification can now begin");
                    return;
                }
            }

            await _delay(_pollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private IReadOnlySet<int> SafeGetProcessIds()
    {
        try
        {
            return _processIds();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Could not inspect {ProcessName}; continuing without the installer continuation check",
                ContinuationProcessName);
            return new HashSet<int>();
        }
    }

    private static IReadOnlySet<int> GetContinuationProcessIds()
    {
        var result = new HashSet<int>();
        foreach (var process in Process.GetProcessesByName(ContinuationProcessName))
        {
            using (process)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        result.Add(process.Id);
                    }
                }
                catch (InvalidOperationException)
                {
                    // The process ended between enumeration and inspection.
                }
            }
        }

        return result;
    }
}
