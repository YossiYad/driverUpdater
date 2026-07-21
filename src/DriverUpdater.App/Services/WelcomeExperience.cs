using DriverUpdater.Core.Options;

namespace DriverUpdater.App.Services;

internal static class WelcomeExperience
{
    internal const string CurrentVersion = "0.1.40";

    internal static bool ShouldShow(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Onboarding ??= new OnboardingSettings();
        return settings.Onboarding.ShowOnStartup;
    }

    internal static void RecordChoice(AppSettings settings, bool showOnStartup)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Onboarding ??= new OnboardingSettings();
        settings.Onboarding.LastShownVersion = CurrentVersion;
        settings.Onboarding.ShowOnStartup = showOnStartup;
    }
}
