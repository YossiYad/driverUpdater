namespace DriverUpdater.Core.Abstractions;

// Headless scan (and optionally install) driven by the Windows scheduled task. Runs with
// no UI: it scans drivers, queries the enabled update sources, persists the results to the
// driver cache so the next interactive launch reflects them, and - when asked - installs
// the confirmed updates unattended through the install pipeline.
public interface IScheduledScanRunner
{
    Task RunAsync(bool installUpdates, CancellationToken cancellationToken = default);
}
