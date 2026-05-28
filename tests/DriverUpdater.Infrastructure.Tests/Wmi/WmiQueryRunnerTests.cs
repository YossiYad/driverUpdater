using DriverUpdater.Infrastructure.Wmi;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using System.Management;

namespace DriverUpdater.Infrastructure.Tests.Wmi;

public class WmiQueryRunnerTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task QueryAsync_returns_at_least_one_signed_driver_from_local_wmi()
    {
        var runner = new WmiQueryRunner(NullLogger<WmiQueryRunner>.Instance);
        var count = 0;
        var sample = (string?)null;

        try
        {
            await foreach (var row in runner.QueryAsync(
                               "\\\\.\\root\\CIMV2",
                               "SELECT DeviceID, DeviceName FROM Win32_PnPSignedDriver",
                               CancellationToken.None))
            {
                count++;
                if (sample is null && row.TryGetValue("DeviceName", out var deviceName))
                {
                    sample = deviceName?.ToString();
                }
                if (count >= 5)
                {
                    break;
                }
            }
        }
        catch (ManagementException ex) when (ex.Message.Contains("Access denied", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        count.Should().BeGreaterThan(0, "every Windows machine has signed drivers");
        sample.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task QueryAsync_throws_for_empty_scope()
    {
        var runner = new WmiQueryRunner(NullLogger<WmiQueryRunner>.Instance);

        var act = async () =>
        {
            await foreach (var _ in runner.QueryAsync("   ", "SELECT * FROM Win32_PnPSignedDriver"))
            {
            }
        };

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task QueryAsync_throws_for_empty_query()
    {
        var runner = new WmiQueryRunner(NullLogger<WmiQueryRunner>.Instance);

        var act = async () =>
        {
            await foreach (var _ in runner.QueryAsync("\\\\.\\root\\CIMV2", " "))
            {
            }
        };

        await act.Should().ThrowAsync<ArgumentException>();
    }
}
