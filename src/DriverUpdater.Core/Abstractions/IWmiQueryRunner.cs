namespace DriverUpdater.Core.Abstractions;

public interface IWmiQueryRunner
{
    IAsyncEnumerable<IReadOnlyDictionary<string, object?>> QueryAsync(
        string scope,
        string wqlQuery,
        CancellationToken cancellationToken = default);
}
