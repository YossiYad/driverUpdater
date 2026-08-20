using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DriverUpdater.Core.Models;

namespace DriverUpdater.App.ViewModels;

/// <summary>One driver in the plan, with the AI reasoning that put it where it is.</summary>
public sealed record AiUpdatePlanEntry(
    DriverRowViewModel Row,
    string Reason,
    AiRiskLevel Risk,
    bool IsEndorsed);

/// <summary>
/// What the AI decided after the scan: the updates it endorsed, the ones it left out, and the
/// risk tolerance those decisions were made against.
/// </summary>
public sealed record AiUpdatePlan(
    IReadOnlyList<AiUpdatePlanEntry> Endorsed,
    IReadOnlyList<AiUpdatePlanEntry> Skipped,
    AiAutoUpdateRiskTolerance Tolerance);

/// <summary>
/// Backs the window shown before "Update with AI" installs anything. The endorsed updates are
/// listed with the reason the AI gave and can be unticked one by one; the ones it left out are
/// listed too, so the window answers both "what will be installed" and "why was this skipped".
/// </summary>
public partial class AiUpdatePlanViewModel : ObservableObject
{
    public AiUpdatePlanViewModel(AiUpdatePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        Tolerance = plan.Tolerance;

        foreach (var entry in plan.Endorsed)
        {
            var item = new AiUpdatePlanItemViewModel(entry);
            item.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(AiUpdatePlanItemViewModel.IsSelected))
                {
                    OnPropertyChanged(nameof(SelectedCount));
                    OnPropertyChanged(nameof(HasSelection));
                    OnPropertyChanged(nameof(SelectionText));
                }
            };
            Endorsed.Add(item);
        }

        foreach (var entry in plan.Skipped)
        {
            Skipped.Add(new AiUpdatePlanItemViewModel(entry));
        }
    }

    public ObservableCollection<AiUpdatePlanItemViewModel> Endorsed { get; } = new();

    public ObservableCollection<AiUpdatePlanItemViewModel> Skipped { get; } = new();

    public AiAutoUpdateRiskTolerance Tolerance { get; }

    /// <summary>Ticked means: install what the AI picks from now on without showing this window.</summary>
    [ObservableProperty]
    private bool _doNotShowAgain;

    public bool HasSkipped => Skipped.Count > 0;

    public int SelectedCount => Endorsed.Count(item => item.IsSelected);

    public bool HasSelection => SelectedCount > 0;

    public string SelectionText => SelectedCount == 1
        ? "1 update will be installed"
        : $"{SelectedCount} updates will be installed";

    public string ToleranceText => Tolerance == AiAutoUpdateRiskTolerance.SafeAndCaution
        ? "Risk tolerance: safe and caution updates"
        : "Risk tolerance: safe updates only";

    public string SkippedHeaderText => Skipped.Count == 1
        ? "1 update the AI left out"
        : $"{Skipped.Count} updates the AI left out";

    public IReadOnlyList<DriverRowViewModel> SelectedRows =>
        Endorsed.Where(item => item.IsSelected).Select(item => item.Entry.Row).ToArray();
}

public partial class AiUpdatePlanItemViewModel : ObservableObject
{
    public AiUpdatePlanItemViewModel(AiUpdatePlanEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Entry = entry;
        _isSelected = entry.IsEndorsed;
    }

    public AiUpdatePlanEntry Entry { get; }

    [ObservableProperty]
    private bool _isSelected;

    public string DeviceName => Entry.Row.DeviceName;

    public string Reason => Entry.Reason;

    public string VersionText => Entry.Row.AvailableVersionText is { Length: > 0 } available
        ? $"{Entry.Row.CurrentVersionText ?? "Unknown"} to {available}"
        : Entry.Row.CurrentVersionText ?? "Unknown";

    public string SourceText => Entry.Row.SourceText ?? "Unknown source";

    public IReadOnlyList<string> Sources =>
        Entry.Row.AvailableUpdate?.AiVerification?.Sources ?? Array.Empty<string>();

    public bool HasSources => Sources.Count > 0;

    public string SourcesText => string.Join(Environment.NewLine, Sources);

    public string RiskText => Entry.Risk switch
    {
        AiRiskLevel.Safe => "Safe",
        AiRiskLevel.Caution => "Caution",
        AiRiskLevel.HighRisk => "High risk",
        _ => "Not rated"
    };
}
