using DriverUpdater.App.Services;
using DriverUpdater.Core.Options;
using FluentAssertions;

namespace DriverUpdater.App.Tests.Services;

public class WelcomeExperienceTests
{
    [Fact]
    public void New_user_sees_the_welcome_guide_once()
    {
        var settings = new AppSettings();

        WelcomeExperience.ShouldShow(settings).Should().BeTrue();

        WelcomeExperience.MarkShown(settings);

        settings.Onboarding.LastShownVersion.Should().Be(WelcomeExperience.CurrentVersion);
        WelcomeExperience.ShouldShow(settings).Should().BeFalse();
    }

    [Fact]
    public void Older_welcome_marker_shows_the_current_guide()
    {
        var settings = new AppSettings
        {
            Onboarding = new OnboardingSettings { LastShownVersion = "0.1.35" }
        };

        WelcomeExperience.ShouldShow(settings).Should().BeTrue();
    }

    [Fact]
    public void Missing_onboarding_section_is_repaired()
    {
        var settings = new AppSettings { Onboarding = null! };

        WelcomeExperience.ShouldShow(settings).Should().BeTrue();

        settings.Onboarding.Should().NotBeNull();
    }
}
