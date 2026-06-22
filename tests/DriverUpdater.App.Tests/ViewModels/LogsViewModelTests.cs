using System.IO;
using DriverUpdater.App.ViewModels;
using FluentAssertions;

namespace DriverUpdater.App.Tests.ViewModels;

public class LogsViewModelTests
{
    [Fact]
    public void FindTestResultsDirectory_finds_workspace_test_logs()
    {
        var result = LogsViewModel.FindTestResultsDirectory();

        result.Should().NotBeNull();
        Directory.Exists(result).Should().BeTrue();
        result.Should().EndWith(Path.Combine("artifacts", "test-results"));
    }
}
