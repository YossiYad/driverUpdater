using DriverUpdater.Infrastructure.PnPUtil;
using FluentAssertions;

namespace DriverUpdater.Infrastructure.Tests.PnPUtil;

public class SetupApiForceDriverBinderTests
{
    [Fact]
    public void ParseStagedDrivers_reads_each_inf_with_its_version()
    {
        const string output = """
            Instance ID: ROOT\SYSTEM\0001
            Matching drivers:

            Driver Name:    oem42.inf
            Original Name:  ViGEmBus.inf
            Provider Name:  Nefarius Software Solutions e.U.
            Driver Version: 09/12/2026 1.22.0.0
            Driver Rank:    0xFF0000

            Driver Name:    oem17.inf
            Original Name:  ViGEmBus.inf
            Provider Name:  Nefarius Software Solutions e.U.
            Driver Version: 04/02/2024 1.21.442.0
            Driver Rank:    0xFF2000
            """;

        var staged = SetupApiForceDriverBinder.ParseStagedDrivers(output);

        staged.Should().HaveCount(2);
        staged[0].InfName.Should().Be("oem42.inf");
        staged[0].Version.Should().Be(new Version(1, 22, 0, 0));
        staged[1].InfName.Should().Be("oem17.inf");
        staged[1].Version.Should().Be(new Version(1, 21, 442, 0));
    }

    [Fact]
    public void ParseStagedDrivers_keeps_an_entry_whose_version_cannot_be_read_but_marks_it_unknown()
    {
        const string output = """
            Driver Name:    oem9.inf
            Driver Version: not a version
            """;

        var staged = SetupApiForceDriverBinder.ParseStagedDrivers(output);

        staged.Should().ContainSingle();
        staged[0].InfName.Should().Be("oem9.inf");
        staged[0].Version.Should().BeNull();
    }

    [Fact]
    public void ParseStagedDrivers_returns_nothing_for_empty_output()
    {
        SetupApiForceDriverBinder.ParseStagedDrivers(null).Should().BeEmpty();
        SetupApiForceDriverBinder.ParseStagedDrivers("   ").Should().BeEmpty();
        SetupApiForceDriverBinder.ParseStagedDrivers("No matching drivers.").Should().BeEmpty();
    }
}
