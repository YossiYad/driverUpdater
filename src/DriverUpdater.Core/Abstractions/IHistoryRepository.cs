using DriverUpdater.Core.Models;

namespace DriverUpdater.Core.Abstractions;

public interface IHistoryRepository
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task UpsertOperationAsync(UpdateOperation operation, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<UpdateOperation>> ListOperationsAsync(int limit = 200, CancellationToken cancellationToken = default);

    Task<UpdateOperation?> GetOperationAsync(Guid operationId, CancellationToken cancellationToken = default);
}
