using System.Globalization;
using System.Windows;
using DriverUpdater.Core.Models;
using DriverUpdater.Core.Options;
using DriverUpdater.Services.Ai;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DriverUpdater.App.Services;

public sealed class AiQuotaNotificationService : IDisposable
{
    private readonly GeminiQuotaGate _quotaGate;
    private readonly IOptionsMonitor<AiSettings> _settings;
    private readonly ILocalizationService _localization;
    private readonly ILogger<AiQuotaNotificationService> _logger;
    private bool _started;

    public AiQuotaNotificationService(
        GeminiQuotaGate quotaGate,
        IOptionsMonitor<AiSettings> settings,
        ILocalizationService localization,
        ILogger<AiQuotaNotificationService> logger)
    {
        ArgumentNullException.ThrowIfNull(quotaGate);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(localization);
        ArgumentNullException.ThrowIfNull(logger);
        _quotaGate = quotaGate;
        _settings = settings;
        _localization = localization;
        _logger = logger;
    }

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _quotaGate.QuotaExceeded += OnQuotaExceeded;
        _started = true;
    }

    private void OnQuotaExceeded(object? sender, GeminiQuotaExceededEventArgs e)
    {
        var apiKeys = _settings.CurrentValue.GetGeminiApiKeys();
        if (apiKeys.Count > 0 && !_quotaGate.AreAllBlocked(apiKeys))
        {
            _logger.LogInformation(
                "A Gemini API key reached its quota, but another configured key is available for fallback");
            return;
        }

        var application = Application.Current;
        if (application is null)
        {
            return;
        }

        _ = application.Dispatcher.InvokeAsync(() => ShowNotification(e));
    }

    private void ShowNotification(GeminiQuotaExceededEventArgs notice)
    {
        try
        {
            var title = TryFindString("AiQuota.Title") ?? "AI quota reached";
            var messageKey = notice.IsDailyQuota
                ? "AiQuota.DailyMessage"
                : "AiQuota.TemporaryMessage";
            var fallback = notice.IsDailyQuota
                ? "The Gemini AI request and token quota has been used up. Daily quotas reset at midnight Pacific time. AI should be available again around {0}. Driver scanning and updates can continue without AI."
                : "Gemini is temporarily limiting AI requests. AI should be available again around {0}. Driver scanning and updates can continue without AI.";
            var template = TryFindString(messageKey) ?? fallback;
            var culture = _localization.CurrentLanguage == AppLanguage.Hebrew
                ? CultureInfo.GetCultureInfo("he-IL")
                : CultureInfo.GetCultureInfo("en-US");
            var localRetryTime = notice.RetryAtUtc.ToLocalTime().ToString("f", culture);
            var message = string.Format(culture, template, localRetryTime);
            var options = _localization.IsRightToLeft
                ? MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign
                : MessageBoxOptions.None;
            var owner = Application.Current?.MainWindow;

            if (owner is null)
            {
                MessageBox.Show(
                    message,
                    title,
                    MessageBoxButton.OK,
                    MessageBoxImage.Information,
                    MessageBoxResult.OK,
                    options);
            }
            else
            {
                MessageBox.Show(
                    owner,
                    message,
                    title,
                    MessageBoxButton.OK,
                    MessageBoxImage.Information,
                    MessageBoxResult.OK,
                    options);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The AI quota notification could not be shown");
        }
    }

    public void Dispose()
    {
        if (!_started)
        {
            return;
        }

        _quotaGate.QuotaExceeded -= OnQuotaExceeded;
        _started = false;
    }

    private static string? TryFindString(string key) =>
        Application.Current?.TryFindResource(key) as string;
}
