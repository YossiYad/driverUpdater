using DriverUpdater.Core.Options;

namespace DriverUpdater.App.Services;

internal static class WelcomeExperience
{
    internal const string CurrentVersion = "0.1.36";

    internal static bool ShouldShow(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Onboarding ??= new OnboardingSettings();
        return !string.Equals(
            settings.Onboarding.LastShownVersion,
            CurrentVersion,
            StringComparison.Ordinal);
    }

    internal static void MarkShown(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Onboarding ??= new OnboardingSettings();
        settings.Onboarding.LastShownVersion = CurrentVersion;
    }
}
