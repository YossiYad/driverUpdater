namespace DriverUpdater.Core.Models;

/// <summary>
/// The devices the user never wants updated, identified by <see cref="DriverInfo.DeviceId"/>.
/// Unlike <see cref="AutoUpdateSelection"/>, which only narrows what an unattended run installs,
/// this list applies to every path: a candidate found for an excluded device is dropped before
/// it is ever offered, scheduled, or installed.
/// </summary>
public sealed record DriverUpdateExclusions(IReadOnlyList<string> DeviceIds)
{
    public static readonly DriverUpdateExclusions Empty = new(Array.Empty<string>());

    public bool Contains(string? deviceId) =>
        !string.IsNullOrWhiteSpace(deviceId)
        && DeviceIds.Contains(deviceId, StringComparer.OrdinalIgnoreCase);

    public bool Equals(DriverUpdateExclusions? other) =>
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
