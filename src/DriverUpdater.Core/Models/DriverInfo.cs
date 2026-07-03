namespace DriverUpdater.Core.Models;

public sealed record DriverInfo(
    string DeviceId,
    string HardwareId,
    string DeviceName,
    DriverCategory Category,
    string Provider,
    string Manufacturer,
    Version? CurrentVersion,
    DateOnly? CurrentDate,
    string? InfName,
    string? InfPath,
    bool IsSigned,
    string DeviceClass)
{
    public IReadOnlyList<string> HardwareIds { get; init; } =
        string.IsNullOrWhiteSpace(HardwareId) ? Array.Empty<string>() : new[] { HardwareId };

    public bool Equals(DriverInfo? other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }
        if (other is null)
        {
            return false;
        }

        return DeviceId == other.DeviceId
            && HardwareId == other.HardwareId
            && DeviceName == other.DeviceName
            && Category == other.Category
            && Provider == other.Provider
            && Manufacturer == other.Manufacturer
            && Equals(CurrentVersion, other.CurrentVersion)
            && CurrentDate == other.CurrentDate
            && InfName == other.InfName
            && InfPath == other.InfPath
            && IsSigned == other.IsSigned
            && DeviceClass == other.DeviceClass
            && HardwareIds.SequenceEqual(other.HardwareIds, StringComparer.OrdinalIgnoreCase);
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(DeviceId);
        hash.Add(HardwareId);
        hash.Add(DeviceName);
        hash.Add(Category);
        hash.Add(Provider);
        hash.Add(Manufacturer);
        hash.Add(CurrentVersion);
        hash.Add(CurrentDate);
        hash.Add(InfName);
        hash.Add(InfPath);
        hash.Add(IsSigned);
        hash.Add(DeviceClass);
        foreach (var id in HardwareIds)
        {
            hash.Add(id, StringComparer.OrdinalIgnoreCase);
        }
        return hash.ToHashCode();
    }

    public static DriverInfo Empty(string deviceId) =>
        new(deviceId,
            HardwareId: string.Empty,
            DeviceName: string.Empty,
            Category: DriverCategory.Other,
            Provider: string.Empty,
            Manufacturer: string.Empty,
            CurrentVersion: null,
            CurrentDate: null,
            InfName: null,
            InfPath: null,
            IsSigned: false,
            DeviceClass: string.Empty);
}
