using CommunityToolkit.Mvvm.ComponentModel;
using DriverUpdater.Core.Models;

namespace DriverUpdater.App.ViewModels;

public partial class ConfirmUpdateDialogViewModel : ObservableObject
{
    public UpdateOperation Operation { get; }

    [ObservableProperty]
    private bool _createRestorePoint = true;

    [ObservableProperty]
    private bool _backupCurrentDriver = true;

    public ConfirmUpdateDialogViewModel(UpdateOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        Operation = operation;
    }

    public string SizeText => Operation.Candidate.SizeBytes > 0
        ? $"Size: {FormatSize(Operation.Candidate.SizeBytes)}"
        : "Size: unknown";

    public string InstallKindText => Operation.Candidate.InstallKind switch
    {
        UpdateInstallKind.WindowsUpdate => "Installs through Windows Update",
        UpdateInstallKind.PnPUtilPackage => "Installs through pnputil",
        UpdateInstallKind.VendorInstaller => "Uses the vendor installer",
        UpdateInstallKind.VendorPage => "Opens the official vendor download page",
        _ => "Install method unknown"
    };

    public bool ShowRiskWarning =>
        Operation.TargetSnapshot.Category is DriverCategory.Display or DriverCategory.Storage;

    public InstallOptions BuildOptions(bool dryRun = false) =>
        new(CreateRestorePoint: CreateRestorePoint,
            BackupCurrentDriver: BackupCurrentDriver,
            DryRun: dryRun);

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024L * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024.0:F1} MB",
        _ => $"{bytes / 1024.0 / 1024.0 / 1024.0:F2} GB"
    };
}
