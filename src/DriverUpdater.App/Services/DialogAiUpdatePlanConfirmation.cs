using System.Windows;
using DriverUpdater.App.ViewModels;
using DriverUpdater.App.Views.Dialogs;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Options;
using Microsoft.Extensions.Logging;

namespace DriverUpdater.App.Services;

public sealed class DialogAiUpdatePlanConfirmation : IAiUpdatePlanConfirmation
{
    private readonly ISettingsStore _settingsStore;
    private readonly ILocalizationService _localization;
    private readonly ILogger<DialogAiUpdatePlanConfirmation> _logger;

    public DialogAiUpdatePlanConfirmation(
        ISettingsStore settingsStore,
        ILocalizationService localization,
        ILogger<DialogAiUpdatePlanConfirmation> logger)
    {
        ArgumentNullException.ThrowIfNull(settingsStore);
        ArgumentNullException.ThrowIfNull(localization);
        ArgumentNullException.ThrowIfNull(logger);
        _settingsStore = settingsStore;
        _localization = localization;
        _logger = logger;
    }

    public async Task<IReadOnlyList<DriverRowViewModel>?> ConfirmAsync(
        AiUpdatePlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var settings = await _settingsStore.LoadAsync(cancellationToken).ConfigureAwait(true);
        settings.Ai ??= new AiSettings();
        if (!settings.Ai.ShowAiUpdatePlanReview)
        {
            _logger.LogInformation(
                "AI update plan review skipped by saved preference: {Count} endorsed update(s) install directly",
                plan.Endorsed.Count);
            return plan.Endorsed.Select(entry => entry.Row).ToArray();
        }

        var dialog = new AiUpdatePlanDialog(plan)
        {
            Owner = Application.Current?.MainWindow,
            FlowDirection = _localization.IsRightToLeft
                ? FlowDirection.RightToLeft
                : FlowDirection.LeftToRight
        };

        if (dialog.ShowDialog() != true)
        {
            _logger.LogInformation(
                "AI update plan cancelled by the user: {Count} endorsed update(s) were not installed",
                plan.Endorsed.Count);
            return null;
        }

        var approved = dialog.ViewModel.SelectedRows;
        if (dialog.ViewModel.DoNotShowAgain)
        {
            settings.Ai.ShowAiUpdatePlanReview = false;
            await _settingsStore.SaveAsync(settings, cancellationToken).ConfigureAwait(true);
        }

        _logger.LogInformation(
            "AI update plan approved: {Approved} of {Endorsed} endorsed update(s) will be installed, showPlanAgain={ShowAgain}",
            approved.Count,
            plan.Endorsed.Count,
            settings.Ai.ShowAiUpdatePlanReview);
        return approved;
    }
}
