namespace DriverUpdater.Services.Sources.Internal.Motherboard;

public interface IMotherboardScraper
{
    Task<IReadOnlyList<MotherboardDriverEntry>> GetDriversAsync(string motherboardModel, CancellationToken cancellationToken = default);
}

public sealed record MotherboardDriverEntry(
    string Title,
    string Version,
    DateOnly ReleaseDate,
    Uri DownloadUrl,
    long? SizeBytes,
    string Category);

public sealed class ScraperUnavailableException : Exception
{
    public ScraperUnavailableException(string message) : base(message) { }
    public ScraperUnavailableException(string message, Exception innerException) : base(message, innerException) { }
}
