using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using Microsoft.Extensions.Logging;

namespace DriverUpdater.App.ViewModels;

/// <summary>
/// The list behind Settings > Schedule > "Choose drivers". It shows the devices of the last
/// scan, remembers which ones the user picked for the custom schedule, and lets entries be
/// removed - including entries whose device no longer shows up in a scan, which would
/// otherwise stay in the file forever with no way to see or delete them.
/// </summary>
public partial class AutoUpdateSelectionViewModel : ObservableObject
{
    private readonly IAutoUpdateSelectionStore _selectionStore;
    private readonly IDriverCacheStore? _driverCacheStore;
    private readonly ILogger<AutoUpdateSelectionViewModel> _logger;

    public ObservableCollection<AutoUpdateDriverRowViewModel> Drivers { get; } = new();

    public ICollectionView DriversView { get; }

    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _searchText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDrivers))]
    [NotifyPropertyChangedFor(nameof(HasNoDrivers))]
    private bool _hasLoaded;

    public bool HasDrivers => Drivers.Count > 0;

    /// <summary>Drives the empty-state message, so it stays hidden until the load finished.</summary>
    public bool HasNoDrivers => HasLoaded && Drivers.Count == 0;

    public int SelectedCount => Drivers.Count(d => d.IsSelected);

    public AutoUpdateSelectionViewModel(
        IAutoUpdateSelectionStore selectionStore,
        ILogger<AutoUpdateSelectionViewModel> logger,
        IDriverCacheStore? driverCacheStore = null)
    {
        ArgumentNullException.ThrowIfNull(selectionStore);
        ArgumentNullException.ThrowIfNull(logger);
        _selectionStore = selectionStore;
        _driverCacheStore = driverCacheStore;
        _logger = logger;
        DriversView = CollectionViewSource.GetDefaultView(Drivers);
        DriversView.Filter = item => item is AutoUpdateDriverRowViewModel row && IsVisible(row);
    }

    partial void OnSearchTextChanged(string value) => DriversView.Refresh();

    [RelayCommand]
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        try
        {
            var selection = await _selectionStore.LoadAsync(cancellationToken).ConfigureAwait(true);
            var selectedIds = new HashSet<string>(selection.DeviceIds, StringComparer.OrdinalIgnoreCase);

            DriverCacheSnapshot? snapshot = null;
            if (_driverCacheStore is not null)
            {
                snapshot = await _driverCacheStore.LoadAsync(cancellationToken).ConfigureAwait(true);
            }

            Drivers.Clear();

            // One row per device, ordered so the picked ones are easy to review first.
            var scanned = snapshot?.Entries ?? Array.Empty<CachedDriverEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in scanned)
            {
                var deviceId = entry.Driver.DeviceId;
                if (string.IsNullOrWhiteSpace(deviceId) || !seen.Add(deviceId))
                {
                    continue;
                }

                Add(new AutoUpdateDriverRowViewModel(
                    deviceId,
                    entry.Driver.DeviceName,
                    entry.Driver.Category.ToString(),
                    entry.Driver.CurrentVersion?.ToString() ?? "unknown",
                    isFromLastScan: true)
                {
                    IsSelected = selectedIds.Contains(deviceId)
                });
            }

            // Picked devices the last scan did not return: a device that was unplugged, or a
            // selection made before the cache was cleared. Listing them is the only way to
            // remove them.
            foreach (var deviceId in selection.DeviceIds.Where(id => !seen.Contains(id)))
            {
                Add(new AutoUpdateDriverRowViewModel(
                    deviceId,
                    deviceId,
                    string.Empty,
                    string.Empty,
                    isFromLastScan: false)
                {
                    IsSelected = true
                });
            }

            HasLoaded = true;
            OnPropertyChanged(nameof(HasDrivers));
        OnPropertyChanged(nameof(HasNoDrivers));
            OnPropertyChanged(nameof(SelectedCount));
            StatusText = Drivers.Count == 0
                ? "No scan results yet. Run a scan in the main window, then come back to pick drivers."
                : $"{SelectedCount} of {Drivers.Count} driver(s) selected for automatic updates.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not load the automatic-update driver list");
            StatusText = $"Could not load the driver list: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RemoveAsync(AutoUpdateDriverRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        row.IsSelected = false;
        if (!row.IsFromLastScan)
        {
            // Nothing left to show for a device that is not in the last scan.
            row.PropertyChanged -= OnRowPropertyChanged;
            Drivers.Remove(row);
            OnPropertyChanged(nameof(HasDrivers));
        OnPropertyChanged(nameof(HasNoDrivers));
        }

        await SaveAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ClearAllAsync()
    {
        foreach (var row in Drivers.ToArray())
        {
            row.IsSelected = false;
            if (!row.IsFromLastScan)
            {
                row.PropertyChanged -= OnRowPropertyChanged;
                Drivers.Remove(row);
            }
        }

        OnPropertyChanged(nameof(HasDrivers));
        OnPropertyChanged(nameof(HasNoDrivers));
        await SaveAsync().ConfigureAwait(true);
    }

    public bool IsVisible(AutoUpdateDriverRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            return true;
        }

        return row.DeviceName.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
            || row.Category.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
    }

    private void Add(AutoUpdateDriverRowViewModel row)
    {
        row.PropertyChanged += OnRowPropertyChanged;
        Drivers.Add(row);
    }

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AutoUpdateDriverRowViewModel.IsSelected))
        {
            return;
        }

        _ = SaveAsync();
    }

    private async Task SaveAsync()
    {
        var deviceIds = Drivers
            .Where(d => d.IsSelected)
            .Select(d => d.DeviceId)
            .ToArray();

        try
        {
            await _selectionStore.SaveAsync(new AutoUpdateSelection(deviceIds)).ConfigureAwait(true);
            OnPropertyChanged(nameof(SelectedCount));
            StatusText = deviceIds.Length == 0
                ? "No driver is updated automatically. The scheduled run will scan and install nothing."
                : $"{deviceIds.Length} of {Drivers.Count} driver(s) selected for automatic updates.";
            _logger.LogInformation(
                "Automatic-update selection saved with {Count} driver(s)", deviceIds.Length);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not save the automatic-update selection");
            StatusText = $"Could not save the selection: {ex.Message}";
        }
    }
}

public partial class AutoUpdateDriverRowViewModel : ObservableObject
{
    public AutoUpdateDriverRowViewModel(
        string deviceId,
        string deviceName,
        string category,
        string currentVersion,
        bool isFromLastScan)
    {
        DeviceId = deviceId;
        DeviceName = deviceName;
        Category = category;
        CurrentVersion = currentVersion;
        IsFromLastScan = isFromLastScan;
    }

    public string DeviceId { get; }

    public string DeviceName { get; }

    public string Category { get; }

    public string CurrentVersion { get; }

    /// <summary>False for a picked device the last scan did not return, so the row can say so.</summary>
    public bool IsFromLastScan { get; }

    public string SubtitleText => IsFromLastScan
        ? string.IsNullOrWhiteSpace(Category) ? CurrentVersion : $"{Category} - installed {CurrentVersion}"
        : "Not in the last scan. Remove it if the device is gone.";

    [ObservableProperty] private bool _isSelected;
}
