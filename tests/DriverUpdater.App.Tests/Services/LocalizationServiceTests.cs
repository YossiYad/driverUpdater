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

    [Fact]
    public void ResolveLanguage_with_system_default_returns_known_value()
    {
        var resolved = LocalizationService.ResolveLanguage(AppLanguage.SystemDefault);
        resolved.Should().BeOneOf(AppLanguage.English, AppLanguage.Hebrew);
    }
}
