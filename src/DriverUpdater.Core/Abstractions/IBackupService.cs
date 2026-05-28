using DriverUpdater.Core.Models;
using DriverUpdater.Core.Results;

namespace DriverUpdater.Core.Abstractions;

public interface IBackupService
{
    Task<Result<BackupArtifact>> BackupDriverAsync(DriverInfo driver, CancellationToken cancellationToken = default);

    Task<Result<bool>> RestoreFromBackupAsync(BackupArtifact artifact, CancellationToken cancellationToken = default);

    IReadOnlyList<BackupArtifact> ListBackups();

    int PurgeBackupsOlderThan(TimeSpan age);
}
