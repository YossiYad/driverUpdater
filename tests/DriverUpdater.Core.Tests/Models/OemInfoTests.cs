using DriverUpdater.Core.Models;
using FluentAssertions;

namespace DriverUpdater.Core.Tests.Models;

public class OemInfoTests : IDisposable
{
    private readonly string _toolPath = Path.Combine(Path.GetTempPath(), $"oem-tool-{Guid.NewGuid():N}.exe");

    [Fact]
    public void ToolInstalled_is_true_only_when_a_nonempty_tool_path_exists()
    {
        File.WriteAllText(_toolPath, string.Empty);

        NewInfo(_toolPath).ToolInstalled.Should().BeTrue();
        NewInfo(null).ToolInstalled.Should().BeFalse();
        NewInfo(string.Empty).ToolInstalled.Should().BeFalse();
        NewInfo(_toolPath + ".missing").ToolInstalled.Should().BeFalse();
    }

    public void Dispose() => File.Delete(_toolPath);

    private static OemInfo NewInfo(string? path) => new(
        OemVendor.Dell,
        "Dell",
        "XPS",
        "Dell Command Update",
        path,
        new Uri("https://www.dell.com/support"));
}
