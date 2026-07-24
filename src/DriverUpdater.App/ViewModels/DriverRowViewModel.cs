using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using DriverUpdater.Core.Models;

namespace DriverUpdater.App.ViewModels;

public partial class DriverRowViewModel : ObservableObject
{
    public DriverInfo Driver { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUpdate))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private DriverStatus _status = DriverStatus.Unknown;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AvailableVersionText))]
    [NotifyPropertyChangedFor(nameof(AvailableDateText))]
    [NotifyPropertyChangedFor(nameof(SourceText))]
    [NotifyPropertyChangedFor(nameof(UpdateActionText))]
    [NotifyPropertyChangedFor(nameof(ConfidenceText))]
    [NotifyPropertyChangedFor(nameof(AiRiskText))]
    [NotifyPropertyChangedFor(nameof(AiRecommendationText))]
    [NotifyPropertyChangedFor(nameof(AiRiskTooltip))]
    [NotifyPropertyChangedFor(nameof(VersionSummaryText))]
    [NotifyPropertyChangedFor(nameof(DriverDetailsTooltip))]
    [NotifyPropertyChangedFor(nameof(HasAiVerdict))]
    [NotifyPropertyChangedFor(nameof(HasAvailableUpdate))]
    [NotifyPropertyChangedFor(nameof(CanUpdate))]
    [NotifyPropertyChangedFor(nameof(CanAskAi))]
    private UpdateCandidate? _availableUpdate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAskAi))]
    private bool _isAiChecking;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUpdate))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(SourceText))]
    [NotifyPropertyChangedFor(nameof(UpdateActionText))]
    [NotifyPropertyChangedFor(nameof(ConfidenceText))]
    [NotifyPropertyChangedFor(nameof(DriverDetailsTooltip))]
    [NotifyPropertyChangedFor(nameof(HasAvailableUpdate))]
    [NotifyPropertyChangedFor(nameof(HasAiVerdict))]
    [NotifyPropertyChangedFor(nameof(AiRiskText))]
    [NotifyPropertyChangedFor(nameof(AiRecommendationText))]
    [NotifyPropertyChangedFor(nameof(AiRiskTooltip))]
    private bool _isUpdateFromCache;

    [ObservableProperty]
    private UpdateOperation? _lastOperation;

    public bool IsScannedThisRun { get; set; } = true;

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
    public string? AvailableVersionText =>
        AvailableUpdate is not { } update ? null
        : IsDateBasedPlaceholderVersion(update) ? "latest"
        : update.NewVersion.ToString();

    // AI discovery falls back to a version literally built from the publish date
    // (yyyy.M.d.0) when the vendor page does not state a real one; showing that as a
    // driver version misleads, so the grid says "latest" instead.
    private static bool IsDateBasedPlaceholderVersion(UpdateCandidate update) =>
        update.SourceUpdateId.StartsWith("ai-latest:", StringComparison.Ordinal)
        && update.NewVersion is { } v
        && v.Major == update.NewDate.Year
        && v.Minor == update.NewDate.Month
        && v.Build == update.NewDate.Day;
    public string? AvailableDateText => AvailableUpdate?.NewDate.ToString("yyyy-MM-dd");
    public string? SourceText => AvailableUpdate is null
        ? null
        : IsUpdateFromCache
            ? $"{AvailableUpdate.Source} (cached)"
            : AvailableUpdate.Source.ToString();
    public string UpdateActionText => IsUpdateFromCache ? string.Empty : AvailableUpdate?.InstallKind switch
    {
        UpdateInstallKind.WindowsUpdate => "Install",
        UpdateInstallKind.PnPUtilPackage => "Install",
        UpdateInstallKind.VendorInstaller => "Install (silent)",
        UpdateInstallKind.VendorPage => "Install / vendor page",
        _ => string.Empty
    };
    public string ConfidenceText => IsUpdateFromCache ? "Cached, not reverified" : AvailableUpdate?.Confidence switch
    {
        UpdateConfidence.Confirmed => "Confirmed",
        UpdateConfidence.Advisory => "Check vendor",
        _ => string.Empty
    };

    public string StatusText => IsUpdateFromCache ? "Cached result, re-scan required" : Status switch
    {
        DriverStatus.Unknown => "Checking...",
        DriverStatus.UpToDate => "Up to date",
        DriverStatus.Outdated => "Update available",
        DriverStatus.NotFound => "No update found",
        DriverStatus.Error => "Check failed",
        DriverStatus.NotUpdated => "Not updated",
        DriverStatus.ManualActionRequired => "Continue on vendor website",
        DriverStatus.RestartRequired => "Restart required",
        DriverStatus.VerificationInconclusive => "Could not verify",
        _ => Status.ToString()
    };

    public string VersionSummaryText => string.IsNullOrWhiteSpace(AvailableVersionText)
        ? CurrentVersionText ?? "Unknown"
        : $"{CurrentVersionText ?? "Unknown"}  to  {AvailableVersionText}";

    public string DriverDetailsTooltip
    {
        get
        {
            var details = new List<string>
            {
                $"Category: {Category}",
                $"Provider: {Provider}",
                $"Installed version: {CurrentVersionText ?? "Unknown"}",
                $"Installed date: {CurrentDateText ?? "Unknown"}"
            };

            if (AvailableUpdate is not null)
            {
                details.Add($"Available version: {AvailableVersionText ?? "Unknown"}");
                details.Add($"Available date: {AvailableDateText ?? "Unknown"}");
                details.Add($"Source: {SourceText ?? "Unknown"}");
                details.Add($"Confidence: {ConfidenceText}");
            }

            return string.Join(Environment.NewLine, details);
        }
    }

    public bool HasAiVerdict => HasAvailableUpdate && AvailableUpdate?.AiVerification is not null;

    public string AiRiskText => !HasAvailableUpdate ? string.Empty : AvailableUpdate?.AiVerification?.Risk switch
    {
        AiRiskLevel.Safe => "Safe",
        AiRiskLevel.Caution => "Caution",
        AiRiskLevel.HighRisk => "High risk",
        _ => string.Empty
    };

    public string? AiRiskTooltip
    {
        get
        {
            if (!HasAvailableUpdate)
            {
                return null;
            }

            var verdict = AvailableUpdate?.AiVerification;
            if (verdict is null)
            {
                return null;
            }
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(verdict.Summary))
            {
                parts.Add(verdict.Summary);
            }
            if (!string.IsNullOrWhiteSpace(verdict.Rationale))
            {
                parts.Add(verdict.Rationale);
            }
            if (!string.IsNullOrWhiteSpace(verdict.InstalledSuitability))
            {
                parts.Add($"Installed driver: {verdict.InstalledSuitability}");
            }
            if (!string.IsNullOrWhiteSpace(verdict.CandidateSuitability))
            {
                parts.Add($"Candidate/latest driver: {verdict.CandidateSuitability}");
            }
            if (!string.IsNullOrWhiteSpace(verdict.RecommendedVersion))
            {
                parts.Add($"Recommended version for this PC: {verdict.RecommendedVersion}");
            }
            if (!string.IsNullOrWhiteSpace(verdict.AdvisorNote))
            {
                parts.Add($"AI advice: {verdict.AdvisorNote}");
            }
            if (!string.IsNullOrWhiteSpace(verdict.LatestKnownVersion))
            {
                parts.Add($"Latest known version: {verdict.LatestKnownVersion}");
            }
            return parts.Count == 0 ? null : string.Join(Environment.NewLine + Environment.NewLine, parts);
        }
    }

    public string AiRecommendationText
    {
        get
        {
            if (!HasAvailableUpdate)
            {
                return string.Empty;
            }

            var verdict = AvailableUpdate?.AiVerification;
            if (verdict is null)
            {
                return string.Empty;
            }
            if (!verdict.IsGenuinelyNewer)
            {
                return "Do not install";
            }
            return verdict.Risk switch
            {
                AiRiskLevel.Safe => "Recommended",
                AiRiskLevel.Caution => "Use caution",
                AiRiskLevel.HighRisk => "Avoid for now",
                _ => "Not enough evidence"
            };
        }
    }

    public bool HasAvailableUpdate => !IsUpdateFromCache && AvailableUpdate is not null;

    public bool CanUpdate => HasAvailableUpdate
        && AvailableUpdate is { } update
        && (Status == DriverStatus.Outdated || update.InstallKind == UpdateInstallKind.VendorPage);

    public bool CanAskAi => !IsAiChecking && IsScannedThisRun;

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
