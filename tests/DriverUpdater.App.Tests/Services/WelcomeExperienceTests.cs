using DriverUpdater.App.Services;
using DriverUpdater.Core.Options;
using FluentAssertions;

namespace DriverUpdater.App.Tests.Services;

public class WelcomeExperienceTests
{
    [Fact]
    public void Current_guide_is_bound_to_the_new_release()
    {
        WelcomeExperience.CurrentVersion.Should().Be("0.1.40");
    }

    [Fact]
    public void New_user_sees_the_welcome_guide_on_every_start_by_default()
    {
        var settings = new AppSettings();

        WelcomeExperience.ShouldShow(settings).Should().BeTrue();

        WelcomeExperience.RecordChoice(settings, showOnStartup: true);

        settings.Onboarding.LastShownVersion.Should().Be(WelcomeExperience.CurrentVersion);
        WelcomeExperience.ShouldShow(settings).Should().BeTrue();
    }

    [Fact]
    public void User_can_disable_automatic_guide_display()
    {
        var settings = new AppSettings();

        WelcomeExperience.RecordChoice(settings, showOnStartup: false);

        WelcomeExperience.ShouldShow(settings).Should().BeFalse();
        settings.Onboarding.ShowOnStartup.Should().BeFalse();
    }

    [Fact]
    public void User_can_restore_automatic_guide_display()
    {
        var settings = new AppSettings
        {
            Onboarding = new OnboardingSettings { ShowOnStartup = false }
        };

        WelcomeExperience.RecordChoice(settings, showOnStartup: true);

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
