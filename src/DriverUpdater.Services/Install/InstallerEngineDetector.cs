using System.Text;

namespace DriverUpdater.Services.Install;

// Identifies which packaging engine built an installer exe by scanning it for the
// engine's marker strings, and maps the engine to its documented unattended flags.
// This lets vendor pages that link an unrecognised exe install silently without a
// per-vendor allowlist entry; the pipeline's Authenticode and expected-publisher
// checks still gate whether the exe is actually executed.
public static class InstallerEngineDetector
{
    private const int MaxScanBytes = 64 * 1024 * 1024;
    private const int BufferSize = 1024 * 1024;

    // Priority order: the Burn section name is the most specific marker; the generic
    // engine names come after. InstallShield version resources are UTF-16, hence the
    // dual encodings for the name markers.
    private static readonly (string Engine, string Arguments, byte[][] Markers)[] Engines =
    [
        ("burn", "/quiet /norestart", [Encoding.ASCII.GetBytes(".wixburn")]),
        ("nullsoft", "/S", [Encoding.ASCII.GetBytes("Nullsoft"), Encoding.Unicode.GetBytes("Nullsoft")]),
        ("inno", "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART", [Encoding.ASCII.GetBytes("Inno Setup"), Encoding.Unicode.GetBytes("Inno Setup")]),
        ("installshield", "/s /v\"/qn REBOOT=ReallySuppress\"", [Encoding.ASCII.GetBytes("InstallShield"), Encoding.Unicode.GetBytes("InstallShield")])
    ];

    public static bool TryDetectSilentArguments(string installerPath, out string arguments, out string engine)
    {
        arguments = string.Empty;
        engine = string.Empty;

        bool[] found;
        try
        {
            found = ScanForMarkers(installerPath);
        }
        catch
        {
            return false;
        }

        for (var i = 0; i < Engines.Length; i++)
        {
            if (found[i])
            {
                engine = Engines[i].Engine;
                arguments = Engines[i].Arguments;
                return true;
            }
        }

        return false;
    }

    private static bool[] ScanForMarkers(string installerPath)
    {
        var found = new bool[Engines.Length];
        var longestMarker = Engines.Max(e => e.Markers.Max(m => m.Length));

        using var stream = File.OpenRead(installerPath);
        var buffer = new byte[BufferSize + longestMarker - 1];
        long scanned = 0;
        var carry = 0;

        while (scanned < MaxScanBytes)
        {
            var read = stream.Read(buffer, carry, BufferSize);
            if (read == 0)
            {
                break;
            }

            var searchable = buffer.AsSpan(0, carry + read);
            for (var i = 0; i < Engines.Length; i++)
            {
                if (found[i])
                {
                    continue;
                }
                foreach (var marker in Engines[i].Markers)
                {
                    if (searchable.IndexOf(marker) >= 0)
                    {
                        found[i] = true;
                        break;
                    }
                }
            }

            if (found.All(f => f))
            {
                break;
            }

            var nextCarry = Math.Min(longestMarker - 1, searchable.Length);
            searchable[^nextCarry..].CopyTo(buffer);
            scanned += searchable.Length - nextCarry;
            carry = nextCarry;
        }

        return found;
    }
}
