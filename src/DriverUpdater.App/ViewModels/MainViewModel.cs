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
    private readonly IOemDetectionService _oemDetectionService;
    private readonly IInstallPipeline _installPipeline;
    private readonly IInstallConfirmation _installConfirmation;
    private readonly IHistoryWindowOpener _historyWindowOpener;
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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOem))]
    [NotifyCanExecuteChangedFor(nameof(OpenOemToolCommand))]
    private OemInfo? _detectedOem;

    public bool HasOem => DetectedOem is not null;

    public string ProgressText => IsScanning
        ? $"Scanning... {ScannedCount} drivers found"
        : ScannedCount > 0
            ? $"{ScannedCount} drivers ({UpdatesFoundCount} updates available)"
            : string.Empty;

    public MainViewModel(
        IDriverScanService scanService,
        IEnumerable<IUpdateSource> updateSources,
        IOemDetectionService oemDetectionService,
        IInstallPipeline installPipeline,
        IInstallConfirmation installConfirmation,
        IHistoryWindowOpener historyWindowOpener,
        ILogger<MainViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(scanService);
        ArgumentNullException.ThrowIfNull(updateSources);
        ArgumentNullException.ThrowIfNull(oemDetectionService);
        ArgumentNullException.ThrowIfNull(installPipeline);
        ArgumentNullException.ThrowIfNull(installConfirmation);
        ArgumentNullException.ThrowIfNull(historyWindowOpener);
        ArgumentNullException.ThrowIfNull(logger);
        _scanService = scanService;
        _updateSources = updateSources.ToArray();
        _oemDetectionService = oemDetectionService;
        _installPipeline = installPipeline;
        _installConfirmation = installConfirmation;
        _historyWindowOpener = historyWindowOpener;
        _logger = logger;

        DriversView = CollectionViewSource.GetDefaultView(Drivers);
        DriversView.Filter = FilterDriver;
    }

    partial void OnCategoryFilterChanged(DriverCategory? value) => DriversView.Refresh();
    partial void OnSearchTextChanged(string value) => DriversView.Refresh();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            DetectedOem = await _oemDetectionService.DetectAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OEM detection failed");
        }
    }

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
    private void OpenHistory()
    {
        _historyWindowOpener.Open();
    }

    [RelayCommand]
    private void Clear()
    {
        Drivers.Clear();
        ScannedCount = 0;
        UpdatesFoundCount = 0;
        StatusText = "Cleared.";
    }

    [RelayCommand]
    private async Task UpdateOutdatedAsync(CancellationToken cancellationToken)
    {
        await RunUpdatesAsync(dryRun: false, cancellationToken).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task DryRunOutdatedAsync(CancellationToken cancellationToken)
    {
        await RunUpdatesAsync(dryRun: true, cancellationToken).ConfigureAwait(true);
    }

    private async Task RunUpdatesAsync(bool dryRun, CancellationToken cancellationToken)
    {
        var targets = Drivers
            .Where(r => r.Status == DriverStatus.Outdated && r.AvailableUpdate is not null)
            .ToArray();

        if (targets.Length == 0)
        {
            StatusText = "No outdated drivers to update.";
            return;
        }

        var firstTarget = targets[0];
        var sampleOperation = UpdateOperation.NewPending(firstTarget.AvailableUpdate!, firstTarget.Driver);
        var confirmResult = _installConfirmation.Confirm(sampleOperation, dryRun);
        if (confirmResult is null)
        {
            StatusText = "Update cancelled.";
            return;
        }
        var options = confirmResult;

        foreach (var row in targets)
        {
            if (row.AvailableUpdate is null)
            {
                continue;
            }
            cancellationToken.ThrowIfCancellationRequested();

            var op = UpdateOperation.NewPending(row.AvailableUpdate, row.Driver);
            StatusText = (dryRun ? "Dry run: " : "Installing: ") + row.DeviceName;

            var finished = await _installPipeline.ExecuteAsync(op, options, new Progress<UpdateOperation>(report =>
            {
                row.Status = MapOperationStatus(report.Status);
                StatusText = $"{report.Status}: {row.DeviceName}";
            }), cancellationToken).ConfigureAwait(true);

            row.Status = MapOperationStatus(finished.Status);
            row.LastOperation = finished;
            _logger.LogInformation("Operation {Id} finished with {Status}", finished.OperationId, finished.Status);
        }

        StatusText = dryRun
            ? $"Dry run completed for {targets.Length} drivers."
            : $"Install completed for {targets.Length} drivers.";
    }

    private static DriverStatus MapOperationStatus(UpdateStatus status) => status switch
    {
        UpdateStatus.Succeeded => DriverStatus.UpToDate,
        UpdateStatus.Failed => DriverStatus.Error,
        UpdateStatus.RolledBack => DriverStatus.Outdated,
        UpdateStatus.Cancelled or UpdateStatus.Skipped => DriverStatus.Outdated,
        _ => DriverStatus.Outdated
    };

    [RelayCommand(CanExecute = nameof(CanOpenOemTool))]
    private void OpenOemTool()
    {
        var oem = DetectedOem;
        if (oem is null)
        {
            return;
        }

        try
        {
            if (oem.ToolInstalled && !string.IsNullOrEmpty(oem.ToolPath))
            {
                var psi = new ProcessStartInfo
                {
                    FileName = oem.ToolPath,
                    UseShellExecute = true
                };
                Process.Start(psi);
                _logger.LogInformation("Launched OEM tool {Tool}", oem.ToolName);
            }
            else
            {
                var psi = new ProcessStartInfo
                {
                    FileName = oem.FallbackUrl.AbsoluteUri,
                    UseShellExecute = true
                };
                Process.Start(psi);
                _logger.LogInformation("Opened OEM URL {Url}", oem.FallbackUrl);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not open OEM tool or URL");
            StatusText = $"Could not open {oem.ToolName}: {ex.Message}";
        }
    }

    private bool CanOpenOemTool() => DetectedOem is not null;

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
