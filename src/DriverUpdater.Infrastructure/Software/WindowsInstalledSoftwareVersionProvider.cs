using DriverUpdater.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace DriverUpdater.Infrastructure.Software;

public sealed class WindowsInstalledSoftwareVersionProvider : IInstalledSoftwareVersionProvider
{
    private const string UninstallKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    private readonly ILogger<WindowsInstalledSoftwareVersionProvider> _logger;

    public WindowsInstalledSoftwareVersionProvider(ILogger<WindowsInstalledSoftwareVersionProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public string? GetVersion(string displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        try
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using var localMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var uninstall = localMachine.OpenSubKey(UninstallKeyPath);
                if (uninstall is null)
                {
                    continue;
                }

                foreach (var subKeyName in uninstall.GetSubKeyNames())
                {
                    using var product = uninstall.OpenSubKey(subKeyName);
                    var installedName = product?.GetValue("DisplayName") as string;
                    if (!string.Equals(installedName, displayName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    return product?.GetValue("DisplayVersion") as string;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the installed version of {Product}", displayName);
        }

        return null;
    }
}
