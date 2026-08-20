using System.Text;
using DriverUpdater.Services.Install;
using FluentAssertions;

namespace DriverUpdater.Services.Tests.Install;

public class InstallerFamilyDetectorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "DriverUpdaterTests",
        Guid.NewGuid().ToString("N"));

    public InstallerFamilyDetectorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Theory]
    [InlineData("Nullsoft Install System v3.08", InstallerFamily.Nsis, "/S")]
    [InlineData("Inno Setup Setup Data (5.5.9)", InstallerFamily.InnoSetup, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART")]
    [InlineData("InstallShield Setup Launcher", InstallerFamily.InstallShield, "/s")]
    [InlineData("Packaged by InstallAware", InstallerFamily.InstallAware, "-INSTALL")]
    public void Detect_names_the_packaging_tool_and_its_silent_switch(
        string marker,
        InstallerFamily expected,
        string expectedArguments)
    {
        var path = WriteInstaller("setup.exe", marker);

        var family = InstallerFamilyDetector.Detect(path);

        family.Should().Be(expected);
        InstallerFamilyDetector.TryGetSilentArguments(family, path, out var arguments).Should().BeTrue();
        arguments.Should().Be(expectedArguments);
    }

    [Fact]
    public void Detect_reads_markers_stored_as_utf16()
    {
        var path = Path.Combine(_root, "wide.exe");
        var bytes = new List<byte>(new byte[512]);
        bytes.AddRange(Encoding.Unicode.GetBytes("Nullsoft Install System v3.10"));
        File.WriteAllBytes(path, bytes.ToArray());

        InstallerFamilyDetector.Detect(path).Should().Be(InstallerFamily.Nsis);
    }

    [Fact]
    public void Detect_reports_msi_from_the_extension()
    {
        var path = WriteInstaller("package.msi", "irrelevant");

        InstallerFamilyDetector.Detect(path).Should().Be(InstallerFamily.Msi);
    }

    [Fact]
    public void An_unrecognised_binary_gets_no_silent_switch()
    {
        var path = WriteInstaller("mystery.exe", "just some bytes");

        var family = InstallerFamilyDetector.Detect(path);

        family.Should().Be(InstallerFamily.Unknown);
        InstallerFamilyDetector.TryGetSilentArguments(family, path, out var arguments).Should().BeFalse();
        arguments.Should().BeEmpty();
    }

    [Fact]
    public void A_wix_bundle_is_driven_with_the_burn_switches()
    {
        var path = WriteInstaller("bundle.exe", "section .wixburn here");

        var family = InstallerFamilyDetector.Detect(path);

        family.Should().Be(InstallerFamily.WixBurn);
        InstallerFamilyDetector.TryGetSilentArguments(family, path, out var arguments).Should().BeTrue();
        arguments.Should().StartWith("/quiet /norestart /log ");
    }

    [Fact]
    public void A_file_that_cannot_be_read_is_not_guessed_at()
    {
        var path = Path.Combine(_root, "missing.exe");

        InstallerFamilyDetector.Detect(path).Should().Be(InstallerFamily.Unknown);
    }

    private string WriteInstaller(string fileName, string marker)
    {
        var path = Path.Combine(_root, fileName);
        var bytes = new List<byte>(new byte[256]);
        bytes.AddRange(Encoding.ASCII.GetBytes(marker));
        bytes.AddRange(new byte[256]);
        File.WriteAllBytes(path, bytes.ToArray());
        return path;
    }
}
