using System.Text;

namespace DriverUpdater.Services.Install;

public enum InstallerFamily
{
    Unknown = 0,
    Msi,
    WixBurn,
    Nsis,
    InnoSetup,
    InstallShield,
    InstallAware
}

/// <summary>
/// Works out how to drive an installer from the file itself. The silent switches used to be
/// keyed off the SourceUpdateId, which only the deterministic vendor sources ever set, so every
/// installer the AI found arrived as "ai-latest:..." and was refused as "not approved for
/// unattended install" no matter how ordinary it was. The packaging tool leaves its own
/// fingerprint in the binary, and that is what actually decides the switches.
/// </summary>
public static class InstallerFamilyDetector
{
    // Enough of the file to carry the packer's stub and resource strings. NSIS and Inno mark
    // themselves in the first few hundred KB; InstallShield and InstallAware keep their strings
    // in resources that can sit further in, hence the wider window.
    private const int ScanBytes = 8 * 1024 * 1024;

    private static readonly (InstallerFamily Family, string[] Markers)[] Signatures =
    [
        (InstallerFamily.WixBurn, [".wixburn", "WixBundleOriginalSource"]),
        (InstallerFamily.Nsis, ["Nullsoft Install System", "NullsoftInst", "Nullsoft.NSIS"]),
        (InstallerFamily.InnoSetup, ["Inno Setup Setup Data", "JR.Inno.Setup", "InnoSetupLdrWindow"]),
        (InstallerFamily.InstallAware, ["InstallAware", "MindVision"]),
        (InstallerFamily.InstallShield, ["InstallShield", "ISSetupStream", "InstallScript"])
    ];

    public static InstallerFamily Detect(string installerPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installerPath);

        if (Path.GetExtension(installerPath).Equals(".msi", StringComparison.OrdinalIgnoreCase))
        {
            return InstallerFamily.Msi;
        }

        byte[] buffer;
        try
        {
            using var stream = File.OpenRead(installerPath);
            var length = (int)Math.Min(stream.Length, ScanBytes);
            buffer = new byte[length];
            stream.ReadExactly(buffer, 0, length);
        }
        catch (Exception)
        {
            // An unreadable file is not a family we can name. The caller falls back to the
            // INF-extraction path, which fails loudly on its own if that does not work either.
            return InstallerFamily.Unknown;
        }

        foreach (var (family, markers) in Signatures)
        {
            foreach (var marker in markers)
            {
                if (Contains(buffer, Encoding.ASCII.GetBytes(marker))
                    || Contains(buffer, Encoding.Unicode.GetBytes(marker)))
                {
                    return family;
                }
            }
        }

        return InstallerFamily.Unknown;
    }

    public static bool TryGetSilentArguments(InstallerFamily family, string installerPath, out string arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installerPath);

        arguments = family switch
        {
            InstallerFamily.WixBurn => $"/quiet /norestart /log \"{Path.ChangeExtension(installerPath, ".log")}\"",
            InstallerFamily.Nsis => "/S",
            InstallerFamily.InnoSetup => "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART",
            // AMD documents -INSTALL for its InstallAware-wrapped packages, and the wrapper
            // ignores /S, so this is the one that actually runs unattended.
            InstallerFamily.InstallAware => "-INSTALL",
            InstallerFamily.InstallShield => "/s",
            _ => string.Empty
        };

        return arguments.Length > 0;
    }

    private static bool Contains(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle) =>
        needle.Length > 0 && haystack.IndexOf(needle) >= 0;
}
