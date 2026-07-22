using System.Windows;
using DriverUpdater.App.Views.Dialogs;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Options;
using DriverUpdater.Services.Ai;
using Microsoft.Extensions.Logging;

namespace DriverUpdater.App.Services;

public sealed class DialogAiScanConfirmation : IAiScanConfirmation
{
    private readonly ISettingsStore _settingsStore;
    private readonly GeminiRequestUsageTracker _usageTracker;
    private readonly ILocalizationService _localization;
    private readonly ILogger<DialogAiScanConfirmation> _logger;

    public DialogAiScanConfirmation(
        ISettingsStore settingsStore,
        GeminiRequestUsageTracker usageTracker,
        ILocalizationService localization,
        ILogger<DialogAiScanConfirmation> logger)
    {
        ArgumentNullException.ThrowIfNull(settingsStore);
        ArgumentNullException.ThrowIfNull(usageTracker);
        ArgumentNullException.ThrowIfNull(localization);
        ArgumentNullException.ThrowIfNull(logger);
        _settingsStore = settingsStore;
        _usageTracker = usageTracker;
        _localization = localization;
        _logger = logger;
    }

    public async Task<bool> ConfirmAsync(
        AiScanUsageEstimate estimate,
        CancellationToken cancellationToken = default)
    {
        var settings = await _settingsStore.LoadAsync(cancellationToken).ConfigureAwait(true);
        settings.Ai ??= new AiSettings();
        if (!settings.Ai.ShowAiScanUsageWarning)
        {
            _logger.LogInformation(
                "AI scan usage warning skipped by saved preference: plannedRequests={PlannedRequests}, model={Model}",
                estimate.PlannedRequests,
                estimate.Model);
            return true;
        }

        var requestsToday = _usageTracker.GetRequestsToday(estimate.Model);
        var dialog = new AiScanWarningDialog(
            estimate,
            requestsToday,
            settings.Ai.GeminiDailyRequestLimit)
        {
            Owner = Application.Current?.MainWindow,
            FlowDirection = _localization.IsRightToLeft
                ? FlowDirection.RightToLeft
                : FlowDirection.LeftToRight
        };

        if (dialog.ShowDialog() != true)
        {
            _logger.LogInformation(
                "AI scan cancelled at usage warning: plannedRequests={PlannedRequests}, requestsToday={RequestsToday}",
                estimate.PlannedRequests,
                requestsToday);
            return false;
        }

        settings.Ai.GeminiDailyRequestLimit = dialog.DailyRequestLimit;
        settings.Ai.ShowAiScanUsageWarning = !dialog.DoNotShowAgain;
        await _settingsStore.SaveAsync(settings, cancellationToken).ConfigureAwait(true);
        _logger.LogInformation(
            "AI scan approved: plannedRequests={PlannedRequests}, requestsToday={RequestsToday}, dailyLimit={DailyLimit}, showWarningAgain={ShowAgain}",
            estimate.PlannedRequests,
            requestsToday,
            dialog.DailyRequestLimit,
            settings.Ai.ShowAiScanUsageWarning);
        return true;
    }
}
