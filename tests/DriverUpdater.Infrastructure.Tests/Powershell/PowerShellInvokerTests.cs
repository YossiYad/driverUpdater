using DriverUpdater.Infrastructure.Powershell;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriverUpdater.Infrastructure.Tests.Powershell;

public class PowerShellInvokerTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task InvokeAsync_echoes_arguments_to_stdout()
    {
        var invoker = new PowerShellInvoker(NullLogger<PowerShellInvoker>.Instance);

        var result = await invoker.InvokeAsync("Write-Output 'hello from test'");

        result.ExitCode.Should().Be(0);
        result.StandardOutput.Should().Contain("hello from test");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task InvokeAsync_propagates_nonzero_exit_code_on_throw()
    {
        var invoker = new PowerShellInvoker(NullLogger<PowerShellInvoker>.Instance);

        var result = await invoker.InvokeAsync("exit 7");

        result.ExitCode.Should().Be(7);
        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task InvokeAsync_throws_on_empty_script()
    {
        var invoker = new PowerShellInvoker(NullLogger<PowerShellInvoker>.Instance);

        var act = async () => await invoker.InvokeAsync(" ");

        await act.Should().ThrowAsync<ArgumentException>();
    }
}
