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
        UpdateInstallKind.VendorInstaller => "Downloads and installs silently (no clicks)",
        UpdateInstallKind.VendorPage => "Opens the official vendor download page",
        _ => "Install method unknown"
    };

    public bool ShowRiskWarning =>
        Operation.TargetSnapshot.Category is DriverCategory.Display or DriverCategory.Storage;

    private AiVerdict? Verdict => Operation.Candidate.AiVerification;

    public bool HasAiVerdict => Verdict is not null;

    public string AiHeader => Verdict?.Risk switch
    {
        AiRiskLevel.Safe => "AI check: Safe",
        AiRiskLevel.Caution => "AI check: Caution",
        AiRiskLevel.HighRisk => "AI check: High risk",
        AiRiskLevel.Unknown => "AI check: Risk unknown",
        _ => "AI check"
    };

    public string AiSummary => Verdict?.Summary ?? string.Empty;

    public string AiRationale => Verdict?.Rationale ?? string.Empty;

    public string AiLatestVersionText =>
        string.IsNullOrWhiteSpace(Verdict?.LatestKnownVersion)
            ? string.Empty
            : $"Latest known version: {Verdict.LatestKnownVersion}";

    public bool HasAiLatestVersion => !string.IsNullOrWhiteSpace(Verdict?.LatestKnownVersion);

    public string AiBackground => Verdict?.Risk switch
    {
        AiRiskLevel.Safe => "#FFE8F5E9",
        AiRiskLevel.Caution => "#FFFFF4E5",
        AiRiskLevel.HighRisk => "#FFFDECEA",
        _ => "#FFF0F0F0"
    };

    public string AiForeground => Verdict?.Risk switch
    {
        AiRiskLevel.Safe => "#FF1B5E20",
        AiRiskLevel.Caution => "#FF8B5A00",
        AiRiskLevel.HighRisk => "#FFB71C1C",
        _ => "#FF424242"
    };

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
