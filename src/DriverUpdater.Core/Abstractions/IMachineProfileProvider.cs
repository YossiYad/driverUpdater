using DriverUpdater.Core.Models;

namespace DriverUpdater.Core.Abstractions;

/// <summary>
/// Reads what this PC is, once per session. Hardware identity does not change while the app
/// runs, and every AI prompt wants it, so implementations are expected to cache.
/// </summary>
public interface IMachineProfileProvider
{
    Task<MachineProfile> GetAsync(CancellationToken cancellationToken = default);
}
