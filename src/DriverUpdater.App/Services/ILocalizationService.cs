using DriverUpdater.Core.Models;

namespace DriverUpdater.App.Services;

public interface ILocalizationService
{
    AppLanguage CurrentLanguage { get; }

    bool IsRightToLeft { get; }

    event EventHandler? LanguageChanged;

    void ApplyLanguage(AppLanguage language);
}
