using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DriverUpdater.App.Services;

namespace DriverUpdater.App.ViewModels;

public sealed partial class SupportViewModel : ObservableObject
{
    public static readonly Uri SponsorsUri = new("https://github.com/sponsors/YossiYad");
    public static readonly Uri FeedbackUri = new(
        "https://github.com/YossiYad/driverUpdater/issues/new?title=Feedback%3A%20&body=Thanks%20for%20using%20DriverUpdater!%0A%0AWhat%20would%20you%20like%20to%20share%3F%0A%0A");

    private readonly IExternalLinkOpener _externalLinkOpener;

    public SupportViewModel(IExternalLinkOpener externalLinkOpener)
    {
        ArgumentNullException.ThrowIfNull(externalLinkOpener);
        _externalLinkOpener = externalLinkOpener;
    }

    [ObservableProperty]
    private string _statusText = string.Empty;

    [RelayCommand]
    private void OpenSponsors()
    {
        StatusText = _externalLinkOpener.Open(SponsorsUri)
            ? "Opening GitHub Sponsors in your browser..."
            : "Could not open GitHub Sponsors. Please try again.";
    }

    [RelayCommand]
    private void LeaveFeedback()
    {
        StatusText = _externalLinkOpener.Open(FeedbackUri)
            ? "Opening a new feedback issue in your browser..."
            : "Could not open the feedback page. Please try again.";
    }
}
