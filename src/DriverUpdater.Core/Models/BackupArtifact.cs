namespace DriverUpdater.Core.Models;

public sealed record BackupArtifact(
    string DriverInfName,
    string DriverDeviceName,
    string BackupFolderPath,
    DateTimeOffset CreatedAt,
    long SizeBytes);
