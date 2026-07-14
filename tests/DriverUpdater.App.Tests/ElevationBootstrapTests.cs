using System.IO;
using FluentAssertions;

namespace DriverUpdater.App.Tests;

public class ElevationBootstrapTests
{
    [Fact]
    public void CreateStartInfo_requests_elevation_and_preserves_arguments()
    {
        var startInfo = ElevationBootstrap.CreateStartInfo(
            @"C:\Apps\DriverUpdater\DriverUpdater.exe",
            ["--scheduled-scan", "value with spaces", "quoted\"value"]);

        startInfo.FileName.Should().Be(@"C:\Apps\DriverUpdater\DriverUpdater.exe");
        startInfo.UseShellExecute.Should().BeTrue();
        startInfo.Verb.Should().Be("runas");
        startInfo.ArgumentList.Should().Equal("--scheduled-scan", "value with spaces", "quoted\"value");
    }

    [Fact]
    public void Manifest_allows_velopack_to_start_hooks_without_elevation()
    {
        var manifestPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "src", "DriverUpdater.App", "app.manifest"));

        var manifest = File.ReadAllText(manifestPath);

        manifest.Should().Contain("requestedExecutionLevel level=\"asInvoker\"");
        manifest.Should().NotContain("requestedExecutionLevel level=\"requireAdministrator\"");
    }
}
