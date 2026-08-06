using System.Globalization;
using DriverUpdater.App.Services;
using DriverUpdater.Core.Models;
using FluentAssertions;

namespace DriverUpdater.App.Tests.Services;

public class LocalizationServiceTests
{
    [Theory]
    [InlineData(AppLanguage.English, AppLanguage.English)]
    [InlineData(AppLanguage.Hebrew, AppLanguage.Hebrew)]
    public void ResolveLanguage_returns_explicit_choice(AppLanguage requested, AppLanguage expected)
    {
        LocalizationService.ResolveLanguage(requested).Should().Be(expected);
    }

    [Theory]
    [InlineData("he-IL", AppLanguage.Hebrew)]
    [InlineData("en-US", AppLanguage.English)]
    [InlineData("ar-SA", AppLanguage.English)]
    public void ResolveLanguage_with_system_default_uses_the_UI_culture(
        string cultureName,
        AppLanguage expected)
    {
        var original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(cultureName);

            LocalizationService.ResolveLanguage(AppLanguage.SystemDefault).Should().Be(expected);
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }
}
