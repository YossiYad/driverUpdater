using System.Globalization;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DriverUpdater.App.Services;
using Wpf.Ui.Controls;

namespace DriverUpdater.App.Views.Dialogs;

public partial class AiScanWarningDialog : FluentWindow
{
    private static readonly Uri GeminiRateLimitsUri = new(
        "https://aistudio.google.com/rate-limit?timeRange=last-28-days");
    private readonly AiScanUsageEstimate _estimate;
    private readonly int _requestsToday;

    public AiScanWarningDialog(
        AiScanUsageEstimate estimate,
        int requestsToday,
        int dailyRequestLimit)
    {
        _estimate = estimate;
        _requestsToday = Math.Max(0, requestsToday);
        InitializeComponent();
        DailyLimitBox.Text = dailyRequestLimit > 0
            ? dailyRequestLimit.ToString(CultureInfo.CurrentCulture)
            : string.Empty;
        RefreshUsageText();
    }

    public bool DoNotShowAgain => DoNotShowAgainBox.IsChecked == true;

    public int DailyRequestLimit { get; private set; }

    private void RefreshUsageText()
    {
        var dailyLimit = TryReadDailyLimit();
        var projectedUsage = _requestsToday + _estimate.PlannedRequests;
        UsageSummaryText.Text = dailyLimit > 0
            ? FormatResource(
                "AiScanWarning.UsageKnown",
                _estimate.PlannedRequests,
                _requestsToday,
                projectedUsage,
                dailyLimit,
                _estimate.Model)
            : FormatResource(
                "AiScanWarning.UsageUnknown",
                _estimate.PlannedRequests,
                _requestsToday,
                _estimate.Model);
        QuotaExplanationText.Text = FindString("AiScanWarning.QuotaExplanation")
            ?? "Google applies quotas per project and model. DriverUpdater can count its own requests, but only AI Studio knows the active quota and usage from other apps. Retries can add requests.";
    }

    private int TryReadDailyLimit() =>
        int.TryParse(DailyLimitBox.Text, NumberStyles.None, CultureInfo.CurrentCulture, out var limit)
            && limit > 0
                ? limit
                : 0;

    private void OnDailyLimitPreviewTextInput(object sender, TextCompositionEventArgs e) =>
        e.Handled = e.Text.Any(character => !char.IsDigit(character));

    private void OnDailyLimitTextChanged(object sender, TextChangedEventArgs e)
    {
        if (UsageSummaryText is not null)
        {
            RefreshUsageText();
        }
    }

    private void OnStartScan(object sender, RoutedEventArgs e)
    {
        DailyRequestLimit = TryReadDailyLimit();
        DialogResult = true;
    }

    private void OnOpenRateLimits(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(GeminiRateLimitsUri.AbsoluteUri)
            {
                UseShellExecute = true
            });
        }
        catch (Exception)
        {
            // Opening the browser is best-effort. The scan can continue without a saved limit.
        }
    }

    private static string FormatResource(string key, params object[] values)
    {
        var template = FindString(key) ?? string.Empty;
        return string.Format(CultureInfo.CurrentCulture, template, values);
    }

    private static string? FindString(string key) =>
        Application.Current?.TryFindResource(key) as string;
}
