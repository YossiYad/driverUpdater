using DriverUpdater.Core.Models;

namespace DriverUpdater.Core.Options;

public sealed class LanguageSettings
{
    public const string SectionName = "Language";

    public AppLanguage Language { get; set; } = AppLanguage.SystemDefault;
}
