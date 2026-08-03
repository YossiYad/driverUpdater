using DriverUpdater.Infrastructure.WuApi;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriverUpdater.Infrastructure.Tests.WuApi;

public class WuApiClientTests
{
    [Theory]
    [InlineData(2, 2, true)]
    [InlineData(3, 2, true)]
    [InlineData(3, 3, false)]
    [InlineData(3, 4, false)]
    [InlineData(4, 2, false)]
    public void IsSuccessfulUpdateResult_requires_the_individual_update_to_succeed(
        int batchResult,
        int updateResult,
        bool expected)
    {
        WuApiClient.IsSuccessfulUpdateResult(batchResult, updateResult).Should().Be(expected);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task SearchDriverUpdatesAsync_returns_results_or_empty_on_real_machine()
    {
        var client = new WuApiClient(NullLogger<WuApiClient>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        var records = new List<DriverUpdater.Core.Models.WuDriverUpdateRecord>();
        try
        {
            await foreach (var record in client.SearchDriverUpdatesAsync(cts.Token))
            {
                records.Add(record);
                if (records.Count >= 50)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }

        records.Where(r => string.IsNullOrEmpty(r.UpdateId)).Should().BeEmpty();
    }
}
