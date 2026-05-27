using DriverUpdater.Infrastructure.PnPUtil;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriverUpdater.Infrastructure.Tests.PnPUtil;

public class PnPUtilRunnerTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task RunAsync_with_question_argument_returns_help_text()
    {
        var runner = new PnPUtilRunner(NullLogger<PnPUtilRunner>.Instance);

        var result = await runner.RunAsync("/?");

        result.ExitCode.Should().Be(0);
        result.StandardOutput.Should().Contain("pnputil");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RunAsync_enum_drivers_returns_at_least_one_oem_entry()
    {
        var runner = new PnPUtilRunner(NullLogger<PnPUtilRunner>.Instance);

        var result = await runner.RunAsync("/enum-drivers");

        result.ExitCode.Should().Be(0);
        result.StandardOutput.ToLowerInvariant().Should().Contain("oem");
    }

    [Fact]
    public async Task RunAsync_throws_on_empty_arguments()
    {
        var runner = new PnPUtilRunner(NullLogger<PnPUtilRunner>.Instance);

        var act = async () => await runner.RunAsync(" ");

        await act.Should().ThrowAsync<ArgumentException>();
    }
}
