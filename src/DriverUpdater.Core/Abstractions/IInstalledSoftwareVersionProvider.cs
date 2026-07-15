namespace DriverUpdater.Core.Abstractions;

public interface IInstalledSoftwareVersionProvider
{
    string? GetVersion(string displayName);
}
