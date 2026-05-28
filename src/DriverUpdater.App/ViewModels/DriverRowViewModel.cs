using CommunityToolkit.Mvvm.ComponentModel;
using DriverUpdater.Core.Models;

namespace DriverUpdater.App.ViewModels;

public partial class DriverRowViewModel : ObservableObject
{
    public DriverInfo Driver { get; }

    [ObservableProperty]
    private DriverStatus _status = DriverStatus.Unknown;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AvailableVersionText))]
    [NotifyPropertyChangedFor(nameof(AvailableDateText))]
    [NotifyPropertyChangedFor(nameof(SourceText))]
    [NotifyPropertyChangedFor(nameof(UpdateActionText))]
    [NotifyPropertyChangedFor(nameof(ConfidenceText))]
    private UpdateCandidate? _availableUpdate;

    [ObservableProperty]
    private UpdateOperation? _lastOperation;

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
}
