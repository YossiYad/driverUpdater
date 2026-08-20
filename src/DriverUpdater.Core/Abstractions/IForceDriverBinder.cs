using DriverUpdater.Core.Models;

namespace DriverUpdater.Core.Abstractions;

/// <summary>
/// Binds a driver that is already in the driver store to the device. pnputil only installs a
/// package when Windows ranks it above what is bound, so an installer can stage a genuinely
/// newer package and leave the device on the old driver with nothing reported as wrong. The
/// SetupAPI path takes a force flag and can complete that switch.
/// </summary>
public interface IForceDriverBinder
{
    Task<ForceBindResult> TryBindStagedDriverAsync(
        DriverInfo device,
        Version? atLeastVersion,
        CancellationToken cancellationToken = default);
}

/// <param name="Attempted">False when nothing staged was newer than what is bound.</param>
public sealed record ForceBindResult(
    bool Attempted,
    bool Succeeded,
    bool RebootRequired,
    string? InfName,
    Version? BoundVersion,
    string? Message);
