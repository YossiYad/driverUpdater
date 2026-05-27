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
