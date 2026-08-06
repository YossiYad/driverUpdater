using DriverUpdater.Infrastructure.WuApi;
using FluentAssertions;

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

}
