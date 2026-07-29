using System.IO.Compression;
using System.Text;
using DriverUpdater.Infrastructure.Archives;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriverUpdater.Infrastructure.Tests.Archives;

public class SharpCompressArchiveExtractorTests
{
    private static SharpCompressArchiveExtractor NewExtractor() =>
        new(NullLogger<SharpCompressArchiveExtractor>.Instance);

    [Fact]
    public void TryExtract_extracts_zip_with_nested_directories()
    {
        using var temp = new TempDir();
        var zipPath = Path.Combine(temp.Path, "driver.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            WriteEntry(zip, "x64/driver.inf", "[Version]");
            WriteEntry(zip, "readme.txt", "hello");
        }
        var destination = Path.Combine(temp.Path, "out");

        var ok = NewExtractor().TryExtract(zipPath, destination, out var error);

        error.Should().BeEmpty();
        ok.Should().BeTrue();
        File.Exists(Path.Combine(destination, "x64", "driver.inf")).Should().BeTrue();
        File.Exists(Path.Combine(destination, "readme.txt")).Should().BeTrue();
    }

    [Fact]
    public void TryExtract_extracts_zip_payload_appended_to_pe_stub()
    {
        using var temp = new TempDir();
        var zipPath = Path.Combine(temp.Path, "payload.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            WriteEntry(zip, "driver.inf", "[Version]");
        }

        var sfxPath = Path.Combine(temp.Path, "installer.exe");
        var stub = new byte[1024];
        stub[0] = 0x4D;
        stub[1] = 0x5A;
        File.WriteAllBytes(sfxPath, [.. stub, .. File.ReadAllBytes(zipPath)]);
        var destination = Path.Combine(temp.Path, "out");

        var ok = NewExtractor().TryExtract(sfxPath, destination, out var error);

        ok.Should().BeTrue();
        error.Should().BeEmpty();
        File.Exists(Path.Combine(destination, "driver.inf")).Should().BeTrue();
    }

    [Fact]
    public void TryExtract_rejects_entries_escaping_destination()
    {
        using var temp = new TempDir();
        var zipPath = Path.Combine(temp.Path, "evil.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            WriteEntry(zip, "../escape.inf", "[Version]");
        }
        var destination = Path.Combine(temp.Path, "out");

        var ok = NewExtractor().TryExtract(zipPath, destination, out var error);

        ok.Should().BeFalse();
        error.Should().Contain("escapes");
        File.Exists(Path.Combine(temp.Path, "escape.inf")).Should().BeFalse();
    }

    [Fact]
    public void TryExtract_fails_cleanly_on_non_archive_exe()
    {
        using var temp = new TempDir();
        var exePath = Path.Combine(temp.Path, "plain.exe");
        var bytes = new byte[4096];
        bytes[0] = 0x4D;
        bytes[1] = 0x5A;
        File.WriteAllBytes(exePath, bytes);

        var ok = NewExtractor().TryExtract(exePath, Path.Combine(temp.Path, "out"), out var error);

        ok.Should().BeFalse();
        error.Should().NotBeEmpty();
    }

    private static void WriteEntry(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name);
        using var stream = entry.Open();
        stream.Write(Encoding.ASCII.GetBytes(content));
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "DriverUpdater.Tests", Guid.NewGuid().ToString("N"));

        public TempDir()
        {
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
            }
        }
    }
}
