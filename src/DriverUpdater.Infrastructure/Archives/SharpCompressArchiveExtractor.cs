using System.Diagnostics;
using DriverUpdater.Core.Abstractions;
using Microsoft.Extensions.Logging;
using SharpCompress.Archives;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Archives.Zip;
using SharpCompress.Readers;

namespace DriverUpdater.Infrastructure.Archives;

public sealed class SharpCompressArchiveExtractor : IArchiveExtractor
{
    // 7z container signature: "7z" BC AF 27 1C. Self-extracting installers prepend a PE
    // stub, so the signature is searched for inside the file instead of at offset 0.
    private static readonly byte[] SevenZipSignature = [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C];

    // Zip local file header ("PK\x03\x04"), searched the same way for zip-based SFX stubs.
    private static readonly byte[] ZipSignature = [0x50, 0x4B, 0x03, 0x04];

    // Cabinet header ("MSCF"). Realtek, Intel and several motherboard vendors ship their
    // driver payload as a cabinet behind a PE stub, which SharpCompress cannot open at all.
    private static readonly byte[] CabSignature = [0x4D, 0x53, 0x43, 0x46];

    private readonly ILogger<SharpCompressArchiveExtractor> _logger;

    public SharpCompressArchiveExtractor(ILogger<SharpCompressArchiveExtractor> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public bool TryExtract(string archivePath, string destinationDirectory, out string errorMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);

        try
        {
            Directory.CreateDirectory(destinationDirectory);
            using (var stream = File.OpenRead(archivePath))
            {
                using var archive = OpenArchive(archivePath, stream);
                if (archive is not null)
                {
                    return ExtractEntries(archive, destinationDirectory, out errorMessage);
                }
            }

            // Nothing SharpCompress reads. A cabinet payload still can be, through the
            // expand.exe that ships with Windows.
            if (TryExtractCabinet(archivePath, destinationDirectory, out errorMessage))
            {
                return true;
            }

            if (errorMessage.Length == 0)
            {
                errorMessage = $"'{Path.GetFileName(archivePath)}' is not a supported archive (zip, 7z, cabinet, or a self-extracting installer containing one).";
            }
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Archive extraction failed for {Path}", archivePath);
            errorMessage = $"Could not extract '{Path.GetFileName(archivePath)}': {ex.Message}";
            return false;
        }
    }

    private static IArchive? OpenArchive(string archivePath, FileStream stream)
    {
        var extension = Path.GetExtension(archivePath);
        if (extension.Equals(".7z", StringComparison.OrdinalIgnoreCase))
        {
            return SevenZipArchive.Open(stream);
        }
        if (extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return ZipArchive.Open(stream);
        }

        var sevenZipOffset = FindSignatureOffset(stream, SevenZipSignature);
        if (sevenZipOffset >= 0)
        {
            return SevenZipArchive.Open(new OffsetStream(stream, sevenZipOffset));
        }

        var zipOffset = FindSignatureOffset(stream, ZipSignature);
        if (zipOffset >= 0)
        {
            try
            {
                var zip = ZipArchive.Open(new OffsetStream(stream, zipOffset));
                _ = zip.Entries.Count;
                return zip;
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    // Windows ships expand.exe for cabinets, so no extra dependency is needed. A self-extracting
    // installer keeps its cabinet behind a PE stub, so the cabinet is carved out to its own file
    // first - expand.exe only accepts a real cabinet.
    private bool TryExtractCabinet(string archivePath, string destinationDirectory, out string errorMessage)
    {
        errorMessage = string.Empty;
        long offset;
        using (var stream = File.OpenRead(archivePath))
        {
            offset = Path.GetExtension(archivePath).Equals(".cab", StringComparison.OrdinalIgnoreCase)
                ? 0
                : FindSignatureOffset(stream, CabSignature);
        }

        if (offset < 0)
        {
            return false;
        }

        var carvedCab = Path.Combine(
            Path.GetTempPath(),
            "DriverUpdater",
            Path.GetFileNameWithoutExtension(archivePath) + "-" + Guid.NewGuid().ToString("N") + ".cab");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(carvedCab)!);
            if (offset == 0)
            {
                carvedCab = archivePath;
            }
            else
            {
                using var source = File.OpenRead(archivePath);
                source.Position = offset;
                using var target = File.Create(carvedCab);
                source.CopyTo(target);
            }

            var expand = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "expand.exe");
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = expand,
                Arguments = $"-F:* \"{carvedCab}\" \"{destinationDirectory}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (process is null)
            {
                errorMessage = "Could not start expand.exe to unpack the cabinet.";
                return false;
            }

            process.WaitForExit(TimeSpan.FromMinutes(5));
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                errorMessage = "expand.exe did not finish unpacking the cabinet in time.";
                return false;
            }

            if (process.ExitCode != 0)
            {
                errorMessage = $"expand.exe could not unpack the cabinet (exit {process.ExitCode}).";
                _logger.LogWarning(
                    "expand.exe failed for {Path}: exit {Exit}, {Error}",
                    archivePath, process.ExitCode, process.StandardError.ReadToEnd());
                return false;
            }

            var extracted = Directory.EnumerateFiles(destinationDirectory, "*", SearchOption.AllDirectories).Any();
            if (!extracted)
            {
                errorMessage = "The cabinet did not contain any files.";
                return false;
            }

            _logger.LogInformation("Unpacked the cabinet inside {Path} with expand.exe", Path.GetFileName(archivePath));
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cabinet extraction failed for {Path}", archivePath);
            errorMessage = $"Could not unpack the cabinet inside '{Path.GetFileName(archivePath)}': {ex.Message}";
            return false;
        }
        finally
        {
            if (!string.Equals(carvedCab, archivePath, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    File.Delete(carvedCab);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    private bool ExtractEntries(IArchive archive, string destinationDirectory, out string errorMessage)
    {
        var root = Path.GetFullPath(destinationDirectory);
        var extracted = 0;
        string? escapingEntry = null;

        bool TryResolveDestination(string? key, out string destination)
        {
            destination = string.Empty;
            if (string.IsNullOrEmpty(key))
            {
                return false;
            }
            destination = Path.GetFullPath(Path.Combine(root, key));
            if (!destination.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                escapingEntry = key;
                return false;
            }
            return true;
        }

        // Solid archives (7z) only decompress sequentially; per-entry random access would
        // re-decompress the whole block for every file. Zip entries are random access and
        // SharpCompress refuses ExtractAllEntries on them, hence the two paths.
        if (archive is SevenZipArchive || archive.IsSolid)
        {
            using var reader = archive.ExtractAllEntries();
            while (reader.MoveToNextEntry())
            {
                if (reader.Entry.IsDirectory)
                {
                    continue;
                }
                if (!TryResolveDestination(reader.Entry.Key, out var destination))
                {
                    if (escapingEntry is not null)
                    {
                        break;
                    }
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                using var output = File.Create(destination);
                reader.WriteEntryTo(output);
                extracted++;
            }
        }
        else
        {
            foreach (var entry in archive.Entries)
            {
                if (entry.IsDirectory)
                {
                    continue;
                }
                if (!TryResolveDestination(entry.Key, out var destination))
                {
                    if (escapingEntry is not null)
                    {
                        break;
                    }
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                using var input = entry.OpenEntryStream();
                using var output = File.Create(destination);
                input.CopyTo(output);
                extracted++;
            }
        }

        if (escapingEntry is not null)
        {
            _logger.LogError("Archive entry {Entry} escapes the extraction directory", escapingEntry);
            errorMessage = $"Archive rejected: entry '{escapingEntry}' escapes the extraction directory.";
            return false;
        }

        if (extracted == 0)
        {
            errorMessage = "The archive did not contain any files.";
            return false;
        }

        errorMessage = string.Empty;
        return true;
    }

    private static long FindSignatureOffset(FileStream stream, byte[] signature)
    {
        // The PE stub before an embedded 7z payload is at most a few MB; cap the scan so a
        // huge non-archive file does not get read end to end.
        const int maxScanBytes = 64 * 1024 * 1024;
        const int bufferSize = 1024 * 1024;

        stream.Position = 0;
        var buffer = new byte[bufferSize + signature.Length - 1];
        long baseOffset = 0;
        var carry = 0;

        while (baseOffset + carry < maxScanBytes)
        {
            var read = stream.Read(buffer, carry, bufferSize);
            if (read == 0)
            {
                break;
            }

            var searchable = carry + read;
            var index = buffer.AsSpan(0, searchable).IndexOf(signature);
            if (index >= 0)
            {
                return baseOffset + index;
            }

            carry = Math.Min(signature.Length - 1, searchable);
            Array.Copy(buffer, searchable - carry, buffer, 0, carry);
            baseOffset += searchable - carry;
        }

        return -1;
    }

    private sealed class OffsetStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _offset;

        public OffsetStream(Stream inner, long offset)
        {
            _inner = inner;
            _offset = offset;
            _inner.Position = offset;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _inner.Length - _offset;

        public override long Position
        {
            get => _inner.Position - _offset;
            set => _inner.Position = value + _offset;
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => origin switch
        {
            SeekOrigin.Begin => _inner.Seek(offset + _offset, SeekOrigin.Begin) - _offset,
            SeekOrigin.Current => _inner.Seek(offset, SeekOrigin.Current) - _offset,
            SeekOrigin.End => _inner.Seek(offset, SeekOrigin.End) - _offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
