using System.Globalization;
using System.Windows;
using DriverUpdater.Core.Models;
using Microsoft.Extensions.Logging;

namespace DriverUpdater.App.Services;

public sealed class LocalizationService : ILocalizationService
{
    public const string EnglishResourcePath = "/DriverUpdater;component/Resources/Strings.en.xaml";
    public const string HebrewResourcePath = "/DriverUpdater;component/Resources/Strings.he.xaml";

    private readonly ILogger<LocalizationService> _logger;

    public LocalizationService(ILogger<LocalizationService> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public AppLanguage CurrentLanguage { get; private set; } = AppLanguage.English;

    public bool IsRightToLeft => CurrentLanguage == AppLanguage.Hebrew;

    public event EventHandler? LanguageChanged;

    public void ApplyLanguage(AppLanguage language)
    {
        var resolved = ResolveLanguage(language);
        if (resolved == CurrentLanguage)
        {
            return;
        }

        if (Application.Current is null)
        {
            CurrentLanguage = resolved;
            return;
        }

        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var existing = dictionaries
            .Where(d => d.Source is not null && (d.Source.OriginalString.EndsWith("Strings.en.xaml", StringComparison.OrdinalIgnoreCase)
                || d.Source.OriginalString.EndsWith("Strings.he.xaml", StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        foreach (var d in existing)
        {
            dictionaries.Remove(d);
        }

        var sourcePath = resolved switch
        {
            AppLanguage.Hebrew => HebrewResourcePath,
            _ => EnglishResourcePath
        };

        try
        {
            var dictionary = new ResourceDictionary
            {
                Source = new Uri(sourcePath, UriKind.RelativeOrAbsolute)
            };
            dictionaries.Add(dictionary);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load language resource {Path}", sourcePath);
            return;
        }

        CurrentLanguage = resolved;
        UpdateFlowDirection();
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    internal static AppLanguage ResolveLanguage(AppLanguage requested)
    {
        if (requested != AppLanguage.SystemDefault)
        {
            return requested;
        }
        var ui = CultureInfo.CurrentUICulture;
        if (ui.TwoLetterISOLanguageName.Equals("he", StringComparison.OrdinalIgnoreCase)
            || ui.Name.StartsWith("he", StringComparison.OrdinalIgnoreCase))
        {
            return AppLanguage.Hebrew;
        }
        return AppLanguage.English;
    }

    private void UpdateFlowDirection()
    {
        if (Application.Current is null)
        {
            return;
        }
        var flow = IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        foreach (Window window in Application.Current.Windows)
        {
            window.FlowDirection = flow;
        }
    }
}
