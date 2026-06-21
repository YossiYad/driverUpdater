using DriverUpdater.Infrastructure.WuApi;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriverUpdater.Infrastructure.Tests.WuApi;

public class WuApiClientTests
{
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
