using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using DriverUpdater.Core.Models;

namespace DriverUpdater.App.ViewModels;

public partial class DriverRowViewModel : ObservableObject
{
    public DriverInfo Driver { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUpdate))]
    private DriverStatus _status = DriverStatus.Unknown;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AvailableVersionText))]
    [NotifyPropertyChangedFor(nameof(AvailableDateText))]
    [NotifyPropertyChangedFor(nameof(SourceText))]
    [NotifyPropertyChangedFor(nameof(UpdateActionText))]
    [NotifyPropertyChangedFor(nameof(ConfidenceText))]
    [NotifyPropertyChangedFor(nameof(CanUpdate))]
    private UpdateCandidate? _availableUpdate;

    [ObservableProperty]
    private UpdateOperation? _lastOperation;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyPropertyChangedFor(nameof(IsPreparing))]
    [NotifyPropertyChangedFor(nameof(IsDownloading))]
    [NotifyPropertyChangedFor(nameof(IsInstalling))]
    [NotifyPropertyChangedFor(nameof(HasDeterminateProgress))]
    [NotifyPropertyChangedFor(nameof(DownloadPercent))]
    [NotifyPropertyChangedFor(nameof(ProgressStatusText))]
    private UpdateOperation? _activeOperation;

    private DispatcherTimer? _installTimer;

    public DriverRowViewModel(DriverInfo driver)
    {
        ArgumentNullException.ThrowIfNull(driver);
        Driver = driver;
    }

    public string DeviceName => Driver.DeviceName;
    public string Provider => Driver.Provider;
    public string Manufacturer => Driver.Manufacturer;
    public DriverCategory Category => Driver.Category;
    public string DeviceClass => Driver.DeviceClass;
    public string HardwareId => Driver.HardwareId;
    public string? CurrentVersionText => Driver.CurrentVersion?.ToString();
    public string? CurrentDateText => Driver.CurrentDate?.ToString("yyyy-MM-dd");
    public bool IsSigned => Driver.IsSigned;
    public string? AvailableVersionText => AvailableUpdate?.NewVersion.ToString();
    public string? AvailableDateText => AvailableUpdate?.NewDate.ToString("yyyy-MM-dd");
    public string? SourceText => AvailableUpdate?.Source.ToString();
    public string UpdateActionText => AvailableUpdate?.InstallKind switch
    {
        UpdateInstallKind.WindowsUpdate => "Install",
        UpdateInstallKind.PnPUtilPackage => "Install",
        UpdateInstallKind.VendorInstaller => "Install (silent)",
        UpdateInstallKind.VendorPage => "Open vendor page",
        _ => string.Empty
    };
    public string ConfidenceText => AvailableUpdate?.Confidence switch
    {
        UpdateConfidence.Confirmed => "Confirmed",
        UpdateConfidence.Advisory => "Check vendor",
        _ => string.Empty
    };

    public bool CanUpdate => Status == DriverStatus.Outdated && AvailableUpdate is not null;

    public bool IsBusy => ActiveOperation is { } op
        && !op.IsTerminal
        && op.Status is UpdateStatus.CreatingRestorePoint
            or UpdateStatus.BackingUp
            or UpdateStatus.Downloading
            or UpdateStatus.Installing;

    public bool IsPreparing => ActiveOperation?.Status is UpdateStatus.CreatingRestorePoint or UpdateStatus.BackingUp;
    public bool IsDownloading => ActiveOperation?.Status == UpdateStatus.Downloading;
    public bool IsInstalling => ActiveOperation?.Status == UpdateStatus.Installing;

    public bool HasDeterminateProgress =>
        ActiveOperation is { Status: UpdateStatus.Downloading, TotalBytes: > 0 };

    public double DownloadPercent
    {
        get
        {
            if (ActiveOperation is not { TotalBytes: { } total } op || total <= 0)
            {
                return 0;
            }
            var pct = (double)op.DownloadedBytes / total * 100.0;
            return pct < 0 ? 0 : pct > 100 ? 100 : pct;
        }
    }

    public string ProgressStatusText
    {
        get
        {
            var op = ActiveOperation;
            if (op is null)
            {
                return string.Empty;
            }
            return op.Status switch
            {
                UpdateStatus.Pending => "Waiting...",
                UpdateStatus.CreatingRestorePoint => "Creating restore point...",
                UpdateStatus.BackingUp => "Backing up driver...",
                UpdateStatus.Downloading when op.TotalBytes is { } total && total > 0 =>
                    $"{FormatMb(op.DownloadedBytes)} / {FormatMb(total)} ({DownloadPercent:F0}%)",
                UpdateStatus.Downloading =>
                    $"{FormatMb(op.DownloadedBytes)} downloaded",
                UpdateStatus.Installing when op.InstallStartedAt is { } started =>
                    $"Installing... {FormatElapsed(DateTimeOffset.UtcNow - started)}",
                UpdateStatus.Installing => "Installing...",
                _ => string.Empty
            };
        }
    }

    partial void OnActiveOperationChanged(UpdateOperation? value)
    {
        if (value?.Status == UpdateStatus.Installing)
        {
            StartInstallTimer();
        }
        else
        {
            StopInstallTimer();
        }
    }

    private void StartInstallTimer()
    {
        if (_installTimer is not null)
        {
            return;
        }
        _installTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _installTimer.Tick += (_, _) => OnPropertyChanged(nameof(ProgressStatusText));
        _installTimer.Start();
    }

    private void StopInstallTimer()
    {
        if (_installTimer is null)
        {
            return;
        }
        _installTimer.Stop();
        _installTimer = null;
    }

    private static string FormatMb(long bytes) =>
        bytes >= 1024L * 1024 * 1024
            ? $"{bytes / 1024.0 / 1024.0 / 1024.0:F2} GB"
            : $"{bytes / 1024.0 / 1024.0:F1} MB";

    private static string FormatElapsed(TimeSpan ts)
    {
        if (ts.Ticks < 0)
        {
            ts = TimeSpan.Zero;
        }
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}"
            : $"{ts.Minutes}:{ts.Seconds:00}";
    }
}
