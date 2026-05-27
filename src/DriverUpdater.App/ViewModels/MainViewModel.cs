using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using Microsoft.Extensions.Logging;

namespace DriverUpdater.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IDriverScanService _scanService;
    private readonly IReadOnlyList<IUpdateSource> _updateSources;
    private readonly ILogger<MainViewModel> _logger;

    public ObservableCollection<DriverRowViewModel> Drivers { get; } = new();

    public ICollectionView DriversView { get; }

    public IReadOnlyList<DriverCategory> AvailableCategories { get; } =
        Enum.GetValues<DriverCategory>().ToArray();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    private string _statusText = "Ready. Click Scan to inventory drivers.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    private bool _isScanning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    private int _scannedCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    private int _updatesFoundCount;

    [ObservableProperty]
    private DriverCategory? _categoryFilter;

    [ObservableProperty]
    private string _searchText = string.Empty;

    public string ProgressText => IsScanning
        ? $"Scanning... {ScannedCount} drivers found"
        : ScannedCount > 0
            ? $"{ScannedCount} drivers ({UpdatesFoundCount} updates available)"
            : string.Empty;

    public MainViewModel(
        IDriverScanService scanService,
        IEnumerable<IUpdateSource> updateSources,
        ILogger<MainViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(scanService);
        ArgumentNullException.ThrowIfNull(updateSources);
        ArgumentNullException.ThrowIfNull(logger);
        _scanService = scanService;
        _updateSources = updateSources.ToArray();
        _logger = logger;

        DriversView = CollectionViewSource.GetDefaultView(Drivers);
        DriversView.Filter = FilterDriver;
    }

    partial void OnCategoryFilterChanged(DriverCategory? value) => DriversView.Refresh();
    partial void OnSearchTextChanged(string value) => DriversView.Refresh();

    [RelayCommand(CanExecute = nameof(CanScan), IncludeCancelCommand = true)]
    private async Task ScanAsync(CancellationToken cancellationToken)
    {
        IsScanning = true;
        Drivers.Clear();
        ScannedCount = 0;
        UpdatesFoundCount = 0;
        StatusText = "Scanning drivers via WMI...";
        var stopwatch = Stopwatch.StartNew();

        try
        {
            await foreach (var driver in _scanService.ScanAsync(cancellationToken))
            {
                Drivers.Add(new DriverRowViewModel(driver));
                ScannedCount = Drivers.Count;
            }

            var elapsed = stopwatch.Elapsed;
            StatusText = $"Scan complete. {Drivers.Count} drivers in {elapsed.TotalSeconds:F1}s. Querying update sources...";
            _logger.LogInformation("Scan finished: {Count} drivers in {Elapsed}", Drivers.Count, elapsed);

            await QueryUpdateSourcesAsync(cancellationToken);

            StatusText = $"Done. {Drivers.Count} drivers, {UpdatesFoundCount} updates available.";
        }
        catch (OperationCanceledException)
        {
            StatusText = $"Scan cancelled. {Drivers.Count} drivers collected so far.";
            _logger.LogInformation("Scan cancelled");
        }
        catch (Exception ex)
        {
            StatusText = $"Scan failed: {ex.Message}";
            _logger.LogError(ex, "Scan failed");
        }
        finally
        {
            IsScanning = false;
        }
    }

    private async Task QueryUpdateSourcesAsync(CancellationToken cancellationToken)
    {
        if (_updateSources.Count == 0 || Drivers.Count == 0)
        {
            return;
        }

        var index = BuildHardwareIdIndex();
        var driverSnapshots = Drivers.Select(d => d.Driver).ToArray();

        foreach (var source in _updateSources)
        {
            try
            {
                StatusText = $"Querying {source.DisplayName}...";
                _logger.LogInformation("Querying {Source}", source.DisplayName);

                await foreach (var candidate in source.SearchAsync(driverSnapshots, cancellationToken))
                {
                    if (TryFindRow(index, candidate.ForHardwareId, out var row) && candidate.IsNewerThan(row.Driver))
                    {
                        row.AvailableUpdate = candidate;
                        row.Status = DriverStatus.Outdated;
                        UpdatesFoundCount = CountOutdated();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Source {Source} failed", source.DisplayName);
                StatusText = $"{source.DisplayName} failed: {ex.Message}";
            }
        }
    }

    private Dictionary<string, List<DriverRowViewModel>> BuildHardwareIdIndex()
    {
        var dict = new Dictionary<string, List<DriverRowViewModel>>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Drivers)
        {
            var key = row.HardwareId;
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }
            if (!dict.TryGetValue(key, out var bucket))
            {
                bucket = new List<DriverRowViewModel>();
                dict[key] = bucket;
            }
            bucket.Add(row);
        }
        return dict;
    }

    private static bool TryFindRow(
        Dictionary<string, List<DriverRowViewModel>> index,
        string hardwareId,
        out DriverRowViewModel row)
    {
        if (!string.IsNullOrWhiteSpace(hardwareId) && index.TryGetValue(hardwareId, out var bucket) && bucket.Count > 0)
        {
            row = bucket[0];
            return true;
        }
        row = null!;
        return false;
    }

    private int CountOutdated() => Drivers.Count(d => d.Status == DriverStatus.Outdated);

    private bool CanScan() => !IsScanning;

    [RelayCommand]
    private void Clear()
    {
        Drivers.Clear();
        ScannedCount = 0;
        UpdatesFoundCount = 0;
        StatusText = "Cleared.";
    }

    private bool FilterDriver(object? item)
    {
        if (item is not DriverRowViewModel row)
        {
            return false;
        }

        if (CategoryFilter is { } category && row.Category != category)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var needle = SearchText.Trim();
            return Contains(row.DeviceName, needle)
                || Contains(row.Provider, needle)
                || Contains(row.Manufacturer, needle)
                || Contains(row.HardwareId, needle);
        }

        return true;
    }

    private static bool Contains(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
