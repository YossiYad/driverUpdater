using DriverUpdater.Infrastructure.PnPUtil;
using FluentAssertions;

namespace DriverUpdater.Infrastructure.Tests.PnPUtil;

public class PowerShellDriverStoreBrowserTests
{
    [Fact]
    public void ParsePackages_reads_an_array_of_drivers()
    {
        const string json = """
            [
              {"Driver":"oem12.inf","OriginalFileName":"C:\\Windows\\System32\\DriverStore\\FileRepository\\nv_dispi.inf_amd64_x\\nv_dispi.inf","ProviderName":"NVIDIA","ClassName":"Display","Version":"32.0.15.5222","Date":"\/Date(1717372800000)\/"},
              {"Driver":"oem7.inf","OriginalFileName":null,"ProviderName":"Realtek","ClassName":"MEDIA","Version":"6.0.9629.1","Date":null}
            ]
            """;

        var packages = PowerShellDriverStoreBrowser.ParsePackages(json);

        packages.Should().HaveCount(2);
        packages[0].PublishedName.Should().Be("oem12.inf");
        packages[0].OriginalFileName.Should().Be("nv_dispi.inf");
        packages[0].Version.Should().Be("32.0.15.5222");
        packages[0].Date.Should().Be(new DateOnly(2024, 6, 3));
        packages[1].PublishedName.Should().Be("oem7.inf");
        packages[1].Date.Should().BeNull();
    }

    [Fact]
    public void ParsePackages_reads_a_single_driver_object()
    {
        const string json = """{"Driver":"oem3.inf","ProviderName":"Intel","ClassName":"Net","Version":"23.0.0.5"}""";

        var packages = PowerShellDriverStoreBrowser.ParsePackages(json);

        packages.Should().ContainSingle();
        packages[0].PublishedName.Should().Be("oem3.inf");
    }

    [Fact]
    public void ParsePackages_returns_empty_for_blank_or_invalid_output()
    {
        PowerShellDriverStoreBrowser.ParsePackages(string.Empty).Should().BeEmpty();
        PowerShellDriverStoreBrowser.ParsePackages("not json").Should().BeEmpty();
    }
}
