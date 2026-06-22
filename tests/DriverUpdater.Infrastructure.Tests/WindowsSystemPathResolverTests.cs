using FluentAssertions;

namespace DriverUpdater.Infrastructure.Tests;

public class WindowsSystemPathResolverTests
{
    [Theory]
    [InlineData(true, false, "Sysnative")]
    [InlineData(true, true, "System32")]
    [InlineData(false, false, "System32")]
    public void ResolveNativeSystemDirectory_selects_native_windows_tools(
        bool is64BitOperatingSystem,
        bool is64BitProcess,
        string expectedDirectory)
    {
        var result = WindowsSystemPathResolver.ResolveNativeSystemDirectory(
            @"C:\Windows",
            is64BitOperatingSystem,
            is64BitProcess);

        result.Should().Be($@"C:\Windows\{expectedDirectory}");
    }
}
