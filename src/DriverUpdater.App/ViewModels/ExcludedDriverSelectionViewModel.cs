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
/// The list behind Settings &gt; Sources &gt; "Choose the drivers to leave alone". It shows the
/// devices of the last scan and the tick decides everything: ticked means never update this
/// device, unticked means update it as usual. Nothing is written until Save, which closes the
/// window. Devices no longer returned by a scan are listed too, so unticking one is the way to
/// drop an entry that would otherwise stay in the file forever with no way to see or delete it.
/// </summary>
public partial class ExcludedDriverSelectionViewModel : ObservableObject
{
    private readonly IDriverUpdateExclusionStore _exclusionStore;
    private readonly IDriverCacheStore? _driverCacheStore;
    private readonly ILogger<ExcludedDriverSelectionViewModel> _logger;

    public ObservableCollection<ExcludedDriverRowViewModel> Drivers { get; } = new();

    public ICollectionView DriversView { get; }

    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _isBusy;
    [ObservableProperty] private string _searchText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDrivers))]
    [NotifyPropertyChangedFor(nameof(HasNoDrivers))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _hasLoaded;

    public bool HasDrivers => Drivers.Count > 0;

    /// <summary>Drives the empty-state message, so it stays hidden until the load finished.</summary>
    public bool HasNoDrivers => HasLoaded && Drivers.Count == 0;

    public int ExcludedCount => Drivers.Count(d => d.IsExcluded);

    /// <summary>Raised after a successful save so the window can close itself.</summary>
    public event EventHandler? SaveCompleted;

    public ExcludedDriverSelectionViewModel(
        IDriverUpdateExclusionStore exclusionStore,
        ILogger<ExcludedDriverSelectionViewModel> logger,
        IDriverCacheStore? driverCacheStore = null)
    {
        ArgumentNullException.ThrowIfNull(exclusionStore);
        ArgumentNullException.ThrowIfNull(logger);
        _exclusionStore = exclusionStore;
        _driverCacheStore = driverCacheStore;
        _logger = logger;
        DriversView = CollectionViewSource.GetDefaultView(Drivers);
        DriversView.Filter = item => item is ExcludedDriverRowViewModel row && IsVisible(row);
    }

    partial void OnSearchTextChanged(string value) => DriversView.Refresh();

    [RelayCommand]
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        HasLoaded = false;
        IsBusy = true;
        try
        {
            var exclusions = await _exclusionStore.LoadAsync(cancellationToken).ConfigureAwait(true);
            var excludedIds = new HashSet<string>(exclusions.DeviceIds, StringComparer.OrdinalIgnoreCase);

            DriverCacheSnapshot? snapshot = null;
            if (_driverCacheStore is not null)
            {
                snapshot = await _driverCacheStore.LoadAsync(cancellationToken).ConfigureAwait(true);
            }

            Drivers.Clear();

            var scanned = snapshot?.Entries ?? Array.Empty<CachedDriverEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in scanned)
            {
                var deviceId = entry.Driver.DeviceId;
                if (string.IsNullOrWhiteSpace(deviceId) || !seen.Add(deviceId))
                {
                    continue;
                }

                Add(new ExcludedDriverRowViewModel(
                    deviceId,
                    entry.Driver.DeviceName,
                    entry.Driver.Category.ToString(),
                    entry.Driver.CurrentVersion?.ToString() ?? "unknown",
                    isFromLastScan: true)
                {
                    IsExcluded = excludedIds.Contains(deviceId)
                });
            }

            // Excluded devices the last scan did not return: a device that was unplugged, or an
            // exclusion made before the cache was cleared. Listing them is the only way to
            // remove them.
            foreach (var deviceId in exclusions.DeviceIds.Where(id => !seen.Contains(id)))
            {
                Add(new ExcludedDriverRowViewModel(
                    deviceId,
                    deviceId,
                    string.Empty,
                    string.Empty,
                    isFromLastScan: false)
                {
                    IsExcluded = true
                });
            }

            HasLoaded = true;
            OnPropertyChanged(nameof(HasDrivers));
            OnPropertyChanged(nameof(HasNoDrivers));
            OnPropertyChanged(nameof(ExcludedCount));
            StatusText = Drivers.Count == 0
                ? "No scan results yet. Run a scan in the main window, then come back to pick drivers."
                : DescribeExclusions(ExcludedCount);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not load the excluded-driver list");
            StatusText = $"Could not load the driver list: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public bool IsVisible(ExcludedDriverRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            return true;
        }

        return row.DeviceName.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
            || row.Category.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
    }

    private void Add(ExcludedDriverRowViewModel row)
    {
        row.PropertyChanged += OnRowPropertyChanged;
        Drivers.Add(row);
    }

    // Ticking a row does not write anything - it only keeps the line at the bottom honest
    // about what Save is going to store.
    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ExcludedDriverRowViewModel.IsExcluded))
        {
            return;
        }

        OnPropertyChanged(nameof(ExcludedCount));
        StatusText = DescribeExclusions(ExcludedCount);
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        var deviceIds = Drivers
            .Where(d => d.IsExcluded)
            .Select(d => d.DeviceId)
            .ToArray();

        IsBusy = true;
        try
        {
            await _exclusionStore
                .SaveAsync(new DriverUpdateExclusions(deviceIds), cancellationToken)
                .ConfigureAwait(true);
            OnPropertyChanged(nameof(ExcludedCount));
            StatusText = DescribeExclusions(deviceIds.Length);
            _logger.LogInformation(
                "Driver update exclusions saved with {Count} driver(s)", deviceIds.Length);
            SaveCompleted?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The window stays open so the message is readable and the ticks are not lost.
            _logger.LogError(ex, "Could not save the excluded-driver list");
            StatusText = $"Could not save the list: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanSave() => HasLoaded && !IsBusy;

    private static string DescribeExclusions(int count) => count switch
    {
        0 => "No driver is excluded. Every update found is offered as usual.",
        1 => "1 driver is never updated, even when a newer version is found.",
        _ => $"{count} drivers are never updated, even when a newer version is found."
    };
}

public partial class ExcludedDriverRowViewModel : ObservableObject
{
    public ExcludedDriverRowViewModel(
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

    /// <summary>False for an excluded device the last scan did not return, so the row can say so.</summary>
    public bool IsFromLastScan { get; }

    public string SubtitleText => IsFromLastScan
        ? string.IsNullOrWhiteSpace(Category) ? CurrentVersion : $"{Category} - installed {CurrentVersion}"
        : "Not in the last scan. Remove it if the device is gone.";

    [ObservableProperty] private bool _isExcluded;
}
