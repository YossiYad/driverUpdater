namespace DriverUpdater.Core.Abstractions;

public interface IArchiveExtractor
{
    bool TryExtract(string archivePath, string destinationDirectory, out string errorMessage);
}
