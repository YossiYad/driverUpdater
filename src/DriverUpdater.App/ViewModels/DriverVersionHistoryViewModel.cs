using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;

namespace DriverUpdater.App.ViewModels;

/// <summary>One selectable older version in the history window.</summary>
public sealed partial class DriverVersionOptionViewModel : ObservableObject
{
    public DriverVersionOptionViewModel(DriverVersionRecord record, bool isInstallable)
    {
        ArgumentNullException.ThrowIfNull(record);
        Record = record;
        IsInstallable = isInstallable;
    }

    public DriverVersionRecord Record { get; }

    public bool IsInstallable { get; }

    public string VersionText => Record.Version;

    public string DetailText
    {
        get
        {
            var date = Record.DriverDate?.ToString("yyyy-MM-dd");
            var seen = Record.LastSeenAt.ToLocalTime().ToString("yyyy-MM-dd");
            var origin = string.IsNullOrWhiteSpace(Record.Provider) ? null : Record.Provider;
            var parts = new[]
            {
                date is null ? null : $"driver date {date}",
                origin,
                $"last seen {seen}",
                IsInstallable ? "in Windows driver store" : "no longer in the driver store"
            };
            return string.Join("  |  ", parts.Where(p => p is not null));
        }
    }
}

/// <summary>
/// Backs the per-device version history window: lists every recorded older version and lets the
/// user restore one that is still present in the Windows DriverStore. The actual downgrade runs
/// through the callback the main window supplies, so row state, exclusions, and status updates
/// stay in one place.
/// </summary>
public sealed partial class DriverVersionHistoryViewModel : ObservableObject
{
    private readonly IDriverVersionHistoryStore _history;
    private readonly IDriverStoreBrowser _driverStore;
    private readonly Func<DriverVersionRecord, Task<bool>> _downgradeAsync;

    public DriverVersionHistoryViewModel(
        DriverInfo driver,
        IDriverVersionHistoryStore history,
        IDriverStoreBrowser driverStore,
        Func<DriverVersionRecord, Task<bool>> downgradeAsync)
    {
        ArgumentNullException.ThrowIfNull(driver);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(driverStore);
        ArgumentNullException.ThrowIfNull(downgradeAsync);
        Driver = driver;
        _history = history;
        _driverStore = driverStore;
        _downgradeAsync = downgradeAsync;
    }

    public DriverInfo Driver { get; }

    public string DeviceName => string.IsNullOrWhiteSpace(Driver.DeviceName) ? Driver.DeviceId : Driver.DeviceName;

    public string CurrentVersionText =>
        Driver.CurrentVersion?.ToString() ?? Driver.CurrentDate?.ToString("yyyy-MM-dd") ?? "(unknown)";

    public ObservableCollection<DriverVersionOptionViewModel> Versions { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoVersions))]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreVersionCommand))]
    private bool _isDowngrading;

    public bool HasNoVersions => !IsLoading && Versions.Count == 0;

    public event EventHandler? DowngradeCompleted;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        IsLoading = true;
        try
        {
            var currentVersion = Driver.CurrentVersion?.ToString();
            var records = await _history.GetHistoryAsync(Driver.DeviceId, cancellationToken).ConfigureAwait(true);
            var packages = await _driverStore.EnumeratePackagesAsync(cancellationToken).ConfigureAwait(true);
            var publishedNames = new HashSet<string>(
                packages.Select(p => p.PublishedName),
                StringComparer.OrdinalIgnoreCase);

            Versions.Clear();
            foreach (var record in records
                .Where(r => !string.Equals(r.Version, currentVersion, StringComparison.OrdinalIgnoreCase)))
            {
                var installable = record.InfName is { Length: > 0 } inf
                    && inf.StartsWith("oem", StringComparison.OrdinalIgnoreCase)
                    && publishedNames.Contains(inf);
                Versions.Add(new DriverVersionOptionViewModel(record, installable));
            }

            StatusText = Versions.Count == 0
                ? "No older versions have been recorded for this device yet. History builds up as scans run."
                : $"{Versions.Count} older version(s) recorded. Versions still in the Windows driver store restore without any download.";
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(HasNoVersions));
        }
    }

    private bool CanRestoreVersion(DriverVersionOptionViewModel? option) =>
        !IsDowngrading && option is { IsInstallable: true };

    [RelayCommand(CanExecute = nameof(CanRestoreVersion))]
    private async Task RestoreVersionAsync(DriverVersionOptionViewModel? option)
    {
        if (option is null)
        {
            return;
        }

        IsDowngrading = true;
        StatusText = $"Restoring version {option.VersionText}...";
        try
        {
            var succeeded = await _downgradeAsync(option.Record).ConfigureAwait(true);
            if (succeeded)
            {
                StatusText = $"Done. The device now uses version {option.VersionText}.";
                DowngradeCompleted?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                StatusText = "The downgrade did not complete. See the main window status and logs for details.";
            }
        }
        finally
        {
            IsDowngrading = false;
        }
    }
}
