using DriverUpdater.Services.Sources.Internal;
using FluentAssertions;

namespace DriverUpdater.Services.Tests.Sources.Internal;

public class AmdSocketDetectorTests
{
    [Theory]
    [InlineData("B850M GAMING X WIFI6E", "am5", "b850")]
    [InlineData("X670E AORUS MASTER", "am5", "x670e")]
    [InlineData("X870E NOVA WIFI7", "am5", "x870e")]
    [InlineData("B650E STEEL LEGEND", "am5", "b650e")]
    [InlineData("A620M-K", "am5", "a620")]
    [InlineData("X570 AORUS PRO", "am4", "x570")]
    [InlineData("B550M PRO-VDH WIFI", "am4", "b550")]
    [InlineData("A520M-K", "am4", "a520")]
    [InlineData("TRX50 AERO D", "str5", "trx50")]
    [InlineData("WRX90E SAGE SE", "str5", "wrx90")]
    [InlineData("TRX40 DESIGNARE", "strx4", "trx40")]
    [InlineData("X399 TAICHI", "tr4", "x399")]
    public void ResolveFromProduct_maps_known_chipsets(string boardProduct, string expectedSocket, string expectedSlug)
    {
        var result = AmdSocketDetector.ResolveFromProduct(boardProduct);

        result.Should().NotBeNull();
        result!.Socket.Should().Be(expectedSocket);
        result.ChipsetSlug.Should().Be(expectedSlug);
        result.IsFallback.Should().BeFalse();
    }

    [Theory]
    [InlineData("Some Random Board")]
    [InlineData("To Be Filled By O.E.M.")]
    [InlineData("Z790 AORUS ELITE")]
    public void ResolveFromProduct_returns_null_for_unknown(string boardProduct)
    {
        AmdSocketDetector.ResolveFromProduct(boardProduct).Should().BeNull();
    }
}
