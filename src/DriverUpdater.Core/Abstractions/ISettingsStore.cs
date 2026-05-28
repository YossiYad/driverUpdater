using DriverUpdater.Core.Options;

namespace DriverUpdater.Core.Abstractions;

public interface ISettingsStore
{
    string SettingsPath { get; }

    Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}
