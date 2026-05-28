using System.Text.Json;
using Dapper;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using DriverUpdater.Core.Options;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DriverUpdater.Infrastructure.History;

public sealed class SqliteHistoryRepository : IHistoryRepository
{
    public const string DefaultFolderName = "DriverUpdater";

    private readonly IOptionsMonitor<HistorySettings> _settings;
    private readonly ILogger<SqliteHistoryRepository> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = false
    };

    public SqliteHistoryRepository(IOptionsMonitor<HistorySettings> settings, ILogger<SqliteHistoryRepository> logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);
        _settings = settings;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var path = ResolveDatabasePath();
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var connection = OpenConnection(path);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS UpdateOperations (
                OperationId TEXT PRIMARY KEY,
                Status TEXT NOT NULL,
                StartedAt TEXT NOT NULL,
                CompletedAt TEXT NULL,
                ErrorMessage TEXT NULL,
                BackupPath TEXT NULL,
                RestorePointSequenceNumber TEXT NULL,
                CandidateJson TEXT NOT NULL,
                TargetSnapshotJson TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_UpdateOperations_StartedAt
                ON UpdateOperations(StartedAt DESC);
        ").ConfigureAwait(false);

        _logger.LogInformation("History database ready at {Path}", path);
    }

    public async Task UpsertOperationAsync(UpdateOperation operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = ResolveDatabasePath();
            await using var connection = OpenConnection(path);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            var parameters = new
            {
                OperationId = operation.OperationId.ToString(),
                Status = operation.Status.ToString(),
                StartedAt = operation.StartedAt.ToString("O"),
                CompletedAt = operation.CompletedAt?.ToString("O"),
                operation.ErrorMessage,
                operation.BackupPath,
                operation.RestorePointSequenceNumber,
                CandidateJson = JsonSerializer.Serialize(operation.Candidate, _jsonOptions),
                TargetSnapshotJson = JsonSerializer.Serialize(operation.TargetSnapshot, _jsonOptions)
            };

            await connection.ExecuteAsync(@"
                INSERT INTO UpdateOperations (
                    OperationId, Status, StartedAt, CompletedAt, ErrorMessage,
                    BackupPath, RestorePointSequenceNumber, CandidateJson, TargetSnapshotJson)
                VALUES (
                    @OperationId, @Status, @StartedAt, @CompletedAt, @ErrorMessage,
                    @BackupPath, @RestorePointSequenceNumber, @CandidateJson, @TargetSnapshotJson)
                ON CONFLICT(OperationId) DO UPDATE SET
                    Status = excluded.Status,
                    CompletedAt = excluded.CompletedAt,
                    ErrorMessage = excluded.ErrorMessage,
                    BackupPath = excluded.BackupPath,
                    RestorePointSequenceNumber = excluded.RestorePointSequenceNumber,
                    CandidateJson = excluded.CandidateJson,
                    TargetSnapshotJson = excluded.TargetSnapshotJson;
            ", parameters).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<UpdateOperation>> ListOperationsAsync(int limit = 200, CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
        {
            limit = 200;
        }

        var path = ResolveDatabasePath();
        await using var connection = OpenConnection(path);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var rows = await connection.QueryAsync<HistoryRow>(@"
            SELECT OperationId, Status, StartedAt, CompletedAt, ErrorMessage,
                   BackupPath, RestorePointSequenceNumber, CandidateJson, TargetSnapshotJson
            FROM UpdateOperations
            ORDER BY StartedAt DESC
            LIMIT @Limit;
        ", new { Limit = limit }).ConfigureAwait(false);

        return rows.Select(MapRow).ToArray();
    }

    public async Task<UpdateOperation?> GetOperationAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        var path = ResolveDatabasePath();
        await using var connection = OpenConnection(path);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var row = await connection.QuerySingleOrDefaultAsync<HistoryRow>(@"
            SELECT OperationId, Status, StartedAt, CompletedAt, ErrorMessage,
                   BackupPath, RestorePointSequenceNumber, CandidateJson, TargetSnapshotJson
            FROM UpdateOperations
            WHERE OperationId = @OperationId;
        ", new { OperationId = operationId.ToString() }).ConfigureAwait(false);

        return row is null ? null : MapRow(row);
    }

    internal string ResolveDatabasePath()
    {
        var configured = _settings.CurrentValue.DatabasePath;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            DefaultFolderName,
            "history.db");
    }

    private static SqliteConnection OpenConnection(string path) =>
        new($"Data Source={path}");

    private UpdateOperation MapRow(HistoryRow row)
    {
        var candidate = JsonSerializer.Deserialize<UpdateCandidate>(row.CandidateJson, _jsonOptions)
            ?? throw new InvalidOperationException($"Could not deserialize candidate for operation {row.OperationId}");
        var snapshot = JsonSerializer.Deserialize<DriverInfo>(row.TargetSnapshotJson, _jsonOptions)
            ?? throw new InvalidOperationException($"Could not deserialize snapshot for operation {row.OperationId}");

        return new UpdateOperation(
            OperationId: Guid.Parse(row.OperationId),
            Candidate: candidate,
            TargetSnapshot: snapshot,
            Status: Enum.Parse<UpdateStatus>(row.Status),
            ErrorMessage: row.ErrorMessage,
            BackupPath: row.BackupPath,
            RestorePointSequenceNumber: row.RestorePointSequenceNumber,
            StartedAt: DateTimeOffset.Parse(row.StartedAt, System.Globalization.CultureInfo.InvariantCulture),
            CompletedAt: row.CompletedAt is null ? null : DateTimeOffset.Parse(row.CompletedAt, System.Globalization.CultureInfo.InvariantCulture));
    }

    private sealed class HistoryRow
    {
        public string OperationId { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string StartedAt { get; set; } = string.Empty;
        public string? CompletedAt { get; set; }
        public string? ErrorMessage { get; set; }
        public string? BackupPath { get; set; }
        public string? RestorePointSequenceNumber { get; set; }
        public string CandidateJson { get; set; } = string.Empty;
        public string TargetSnapshotJson { get; set; } = string.Empty;
    }
}
