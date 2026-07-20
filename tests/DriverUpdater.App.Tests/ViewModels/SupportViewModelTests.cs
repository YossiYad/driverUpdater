using DriverUpdater.App.Services;
using DriverUpdater.App.ViewModels;
using FluentAssertions;

namespace DriverUpdater.App.Tests.ViewModels;

public class SupportViewModelTests
{
    [Fact]
    public void OpenSponsors_opens_the_users_GitHub_Sponsors_page()
    {
        var opener = new RecordingExternalLinkOpener(true);
        var viewModel = new SupportViewModel(opener);

        viewModel.OpenSponsorsCommand.Execute(null);

        opener.OpenedUris.Should().ContainSingle()
            .Which.Should().Be(new Uri("https://github.com/sponsors/YossiYad"));
        viewModel.StatusText.Should().Contain("Opening GitHub Sponsors");
    }

    [Fact]
    public void LeaveFeedback_opens_a_prefilled_issue_in_the_project_repository()
    {
        var opener = new RecordingExternalLinkOpener(true);
        var viewModel = new SupportViewModel(opener);

        viewModel.LeaveFeedbackCommand.Execute(null);

        var uri = opener.OpenedUris.Should().ContainSingle().Subject;
        uri.GetLeftPart(UriPartial.Path)
            .Should().Be("https://github.com/YossiYad/driverUpdater/issues/new");
        Uri.UnescapeDataString(uri.Query).Should().Contain("title=Feedback:");
        Uri.UnescapeDataString(uri.Query).Should().Contain("What would you like to share?");
    }

    [Fact]
    public void Failed_browser_launch_is_explained_to_the_user()
    {
        var viewModel = new SupportViewModel(new RecordingExternalLinkOpener(false));

        viewModel.OpenSponsorsCommand.Execute(null);

        viewModel.StatusText.Should().Contain("Could not open GitHub Sponsors");
    }

    private sealed class RecordingExternalLinkOpener(bool result) : IExternalLinkOpener
    {
        public List<Uri> OpenedUris { get; } = new();

        public bool Open(Uri uri)
        {
            OpenedUris.Add(uri);
            return result;
        }
    }
}
