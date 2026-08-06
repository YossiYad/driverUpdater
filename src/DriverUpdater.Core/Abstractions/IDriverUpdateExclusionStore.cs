using DriverUpdater.Core.Models;

namespace DriverUpdater.Core.Abstractions;

/// <summary>
/// Persists the devices the user excluded from updating altogether. Kept out of settings.json
/// for the same reason as <see cref="IAutoUpdateSelectionStore"/>: the exclusion window rewrites
/// it on every checkbox toggle, while the Settings window holds a whole
/// <see cref="Options.AppSettings"/> in memory and would clobber concurrent edits.
/// </summary>
public interface IDriverUpdateExclusionStore
{
    Task<DriverUpdateExclusions> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(DriverUpdateExclusions exclusions, CancellationToken cancellationToken = default);
}
