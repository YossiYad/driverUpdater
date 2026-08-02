namespace DriverUpdater.Core.Models;

/// <summary>
/// The devices the user opted in to unattended updating, identified by <see cref="DriverInfo.DeviceId"/>.
/// Only consulted when <see cref="AutoUpdateScope.SelectedDrivers"/> is active.
/// </summary>
public sealed record AutoUpdateSelection(IReadOnlyList<string> DeviceIds)
{
    public static readonly AutoUpdateSelection Empty = new(Array.Empty<string>());

    public bool Contains(string? deviceId) =>
        !string.IsNullOrWhiteSpace(deviceId)
        && DeviceIds.Contains(deviceId, StringComparer.OrdinalIgnoreCase);

    public bool Equals(AutoUpdateSelection? other) =>
        other is not null && DeviceIds.SequenceEqual(other.DeviceIds, StringComparer.OrdinalIgnoreCase);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var id in DeviceIds)
        {
            hash.Add(id, StringComparer.OrdinalIgnoreCase);
        }
        return hash.ToHashCode();
    }
}
