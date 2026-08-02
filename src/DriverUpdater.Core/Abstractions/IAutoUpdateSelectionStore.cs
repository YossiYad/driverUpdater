using DriverUpdater.Core.Models;

namespace DriverUpdater.Core.Abstractions;

/// <summary>
/// Persists which devices the user picked for unattended updating. Kept out of settings.json
/// because the main grid rewrites it on every checkbox toggle, while the Settings window holds
/// a whole <see cref="Options.AppSettings"/> in memory and would clobber concurrent edits.
/// </summary>
public interface IAutoUpdateSelectionStore
{
    Task<AutoUpdateSelection> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(AutoUpdateSelection selection, CancellationToken cancellationToken = default);
}
