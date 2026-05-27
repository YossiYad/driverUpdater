using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using DriverUpdater.Services.Backup;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriverUpdater.Services.Tests.Backup;

public class RestorePointServiceTests
{
    [Fact]
    public async Task CreateRestorePointAsync_returns_failure_when_powershell_exits_nonzero()
    {
        var fake = new FakePowerShellInvoker(new ProcessResult(1, "", "Access denied"));
        var service = new RestorePointService(fake, NullLogger<RestorePointService>.Instance);

        var result = await service.CreateRestorePointAsync("test");

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("RESTORE_POINT_FAILED");
        result.Error.Message.Should().Contain("Access denied");
    }

    [Fact]
    public async Task CreateRestorePointAsync_parses_powershell_output()
    {
        var output = "SEQ=42;DESC=Before driver update;TIME=2026-05-27T12:34:56.0000000Z\n";
        var fake = new FakePowerShellInvoker(new ProcessResult(0, output, ""));
        var service = new RestorePointService(fake, NullLogger<RestorePointService>.Instance);

        var result = await service.CreateRestorePointAsync("Before driver update");

        result.IsSuccess.Should().BeTrue();
        result.Value.SequenceNumber.Should().Be("42");
        result.Value.Description.Should().Be("Before driver update");
        result.Value.CreatedAt.ToUniversalTime().Should().Be(new DateTimeOffset(2026, 5, 27, 12, 34, 56, TimeSpan.Zero));
    }

    [Fact]
    public async Task CreateRestorePointAsync_returns_parse_failure_on_unrecognized_output()
    {
        var fake = new FakePowerShellInvoker(new ProcessResult(0, "no markers here", ""));
        var service = new RestorePointService(fake, NullLogger<RestorePointService>.Instance);

        var result = await service.CreateRestorePointAsync("anything");

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("RESTORE_POINT_PARSE");
    }

    [Fact]
    public async Task IsSystemRestoreEnabledAsync_returns_true_when_script_outputs_enabled()
    {
        var fake = new FakePowerShellInvoker(new ProcessResult(0, "ENABLED\n", ""));
        var service = new RestorePointService(fake, NullLogger<RestorePointService>.Instance);

        var enabled = await service.IsSystemRestoreEnabledAsync();

        enabled.Should().BeTrue();
    }

    [Fact]
    public async Task IsSystemRestoreEnabledAsync_returns_false_when_script_says_unknown()
    {
        var fake = new FakePowerShellInvoker(new ProcessResult(0, "UNKNOWN\n", ""));
        var service = new RestorePointService(fake, NullLogger<RestorePointService>.Instance);

        var enabled = await service.IsSystemRestoreEnabledAsync();

        enabled.Should().BeFalse();
    }

    [Fact]
    public void ParseRestorePointOutput_parses_seq_desc_time()
    {
        var info = RestorePointService.ParseRestorePointOutput("SEQ=7;DESC=Auto;TIME=2026-01-15T08:00:00.0000000Z");
        info.Should().NotBeNull();
        info!.SequenceNumber.Should().Be("7");
        info.Description.Should().Be("Auto");
    }

    [Fact]
    public void ParseRestorePointOutput_returns_null_when_output_is_garbage()
    {
        RestorePointService.ParseRestorePointOutput("garbage").Should().BeNull();
    }

    private sealed class FakePowerShellInvoker : IPowerShellInvoker
    {
        private readonly ProcessResult _result;
        public List<string> Invocations { get; } = new();

        public FakePowerShellInvoker(ProcessResult result) { _result = result; }

        public Task<ProcessResult> InvokeAsync(string script, CancellationToken cancellationToken = default)
        {
            Invocations.Add(script);
            return Task.FromResult(_result);
        }
    }
}
