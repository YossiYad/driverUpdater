using System.Text;
using DriverUpdater.Services.Install;
using FluentAssertions;

namespace DriverUpdater.Services.Tests.Install;

public class InstallerEngineDetectorTests
{
    [Theory]
    [InlineData("Nullsoft Install System v3.09", "nullsoft", "/S")]
    [InlineData("Inno Setup Setup Data (6.2.0)", "inno", "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART")]
    [InlineData(".wixburn section", "burn", "/quiet /norestart")]
    public void TryDetectSilentArguments_recognizes_ascii_markers(string marker, string expectedEngine, string expectedArguments)
    {
        using var temp = new TempFile(BuildExe(Encoding.ASCII.GetBytes(marker)));

        var ok = InstallerEngineDetector.TryDetectSilentArguments(temp.Path, out var arguments, out var engine);

        ok.Should().BeTrue();
        engine.Should().Be(expectedEngine);
        arguments.Should().Be(expectedArguments);
    }

    [Fact]
    public void TryDetectSilentArguments_recognizes_utf16_installshield_marker()
    {
        using var temp = new TempFile(BuildExe(Encoding.Unicode.GetBytes("InstallShield (R) Setup Launcher")));

        var ok = InstallerEngineDetector.TryDetectSilentArguments(temp.Path, out var arguments, out var engine);

        ok.Should().BeTrue();
        engine.Should().Be("installshield");
        arguments.Should().Contain("/s");
        arguments.Should().Contain("/qn");
    }

    [Fact]
    public void TryDetectSilentArguments_finds_marker_across_buffer_boundary()
    {
        var payload = new byte[2 * 1024 * 1024];
        var marker = Encoding.ASCII.GetBytes("Nullsoft");
        marker.CopyTo(payload, (1024 * 1024) - 4);
        using var temp = new TempFile(BuildExe(payload));

        var ok = InstallerEngineDetector.TryDetectSilentArguments(temp.Path, out _, out var engine);

        ok.Should().BeTrue();
        engine.Should().Be("nullsoft");
    }

    [Fact]
    public void TryDetectSilentArguments_returns_false_without_markers()
    {
        using var temp = new TempFile(BuildExe(new byte[4096]));

        var ok = InstallerEngineDetector.TryDetectSilentArguments(temp.Path, out _, out _);

        ok.Should().BeFalse();
    }

    [Fact]
    public void TryDetectSilentArguments_prefers_burn_over_engine_names_it_embeds()
    {
        var payload = Encoding.ASCII.GetBytes("InstallShield junk .wixburn more");
        using var temp = new TempFile(BuildExe(payload));

        var ok = InstallerEngineDetector.TryDetectSilentArguments(temp.Path, out _, out var engine);

        ok.Should().BeTrue();
        engine.Should().Be("burn");
    }

    private static byte[] BuildExe(byte[] payload)
    {
        var header = new byte[64];
        header[0] = 0x4D;
        header[1] = 0x5A;
        return [.. header, .. payload];
    }

    private sealed class TempFile : IDisposable
    {
        public string Path { get; }

        public TempFile(byte[] content)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "DriverUpdater.Tests", Guid.NewGuid().ToString("N") + ".exe");
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllBytes(Path, content);
        }

        public void Dispose()
        {
            try
            {
                File.Delete(Path);
            }
            catch
            {
            }
        }
    }
}
