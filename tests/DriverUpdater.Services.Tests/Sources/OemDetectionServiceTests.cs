using System.Runtime.CompilerServices;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using DriverUpdater.Services.Sources;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriverUpdater.Services.Tests.Sources;

public class OemDetectionServiceTests
{
    [Theory]
    [InlineData("LENOVO", "ThinkPad X1", OemVendor.Lenovo)]
    [InlineData("Dell Inc.", "XPS 13", OemVendor.Dell)]
    [InlineData("Hewlett-Packard", "EliteBook", OemVendor.Hp)]
    [InlineData("HP", "Pavilion", OemVendor.Hp)]
    [InlineData("Micro-Star International Co., Ltd.", "GS66", OemVendor.Msi)]
    [InlineData("ASUSTeK COMPUTER INC.", "ROG Strix", OemVendor.Asus)]
    [InlineData("Acer", "Aspire 5", OemVendor.Acer)]
    [InlineData("Microsoft Corporation", "Surface Pro 9", OemVendor.MicrosoftSurface)]
    [InlineData("Razer", "Blade 15", OemVendor.Razer)]
    [InlineData("Samsung Electronics Co., Ltd.", "Galaxy Book", OemVendor.Samsung)]
    [InlineData("TOSHIBA", "Satellite", OemVendor.Toshiba)]
    [InlineData("Some Generic Maker", "Mystery Box", OemVendor.Unknown)]
    [InlineData("", "", OemVendor.Unknown)]
    public void MapVendor_recognizes_known_manufacturers(string manufacturer, string model, OemVendor expected)
    {
        OemDetectionService.MapVendor(manufacturer, model).Should().Be(expected);
    }

    [Fact]
    public async Task DetectAsync_returns_oem_info_when_manufacturer_is_known()
    {
        var fake = new FakeWmiRunner(
            new Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, object?>>>
            {
                ["computersystem"] = new[]
                {
                    Row(("Manufacturer", "LENOVO"), ("Model", "ThinkPad X1 Carbon Gen 11"))
                }
            });

        var service = new OemDetectionService(fake, NullLogger<OemDetectionService>.Instance);

        var info = await service.DetectAsync();

        info.Should().NotBeNull();
        info!.Vendor.Should().Be(OemVendor.Lenovo);
        info.Manufacturer.Should().Be("LENOVO");
        info.Model.Should().Be("ThinkPad X1 Carbon Gen 11");
        info.ToolName.Should().Be("Lenovo Vantage");
        info.FallbackUrl.Host.Should().Contain("lenovo.com");
    }

    [Fact]
    public async Task DetectAsync_falls_back_to_baseboard_when_computer_system_has_no_manufacturer()
    {
        var fake = new FakeWmiRunner(
            new Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, object?>>>
            {
                ["computersystem"] = new[]
                {
                    Row(("Manufacturer", null), ("Model", null))
                },
                ["baseboard"] = new[]
                {
                    Row(("Manufacturer", "ASUSTeK COMPUTER INC."), ("Product", "ROG Strix B650"))
                }
            });

        var service = new OemDetectionService(fake, NullLogger<OemDetectionService>.Instance);

        var info = await service.DetectAsync();

        info.Should().NotBeNull();
        info!.Vendor.Should().Be(OemVendor.Asus);
    }

    [Fact]
    public async Task DetectAsync_returns_null_for_unknown_manufacturer()
    {
        var fake = new FakeWmiRunner(
            new Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, object?>>>
            {
                ["computersystem"] = new[]
                {
                    Row(("Manufacturer", "Garage Tinkerer"), ("Model", "Frankenbox"))
                }
            });

        var service = new OemDetectionService(fake, NullLogger<OemDetectionService>.Instance);

        var info = await service.DetectAsync();

        info.Should().BeNull();
    }

    [Fact]
    public void GetToolTemplate_returns_distinct_tools_for_each_vendor()
    {
        var names = Enum.GetValues<OemVendor>()
            .Where(v => v != OemVendor.Unknown)
            .Select(v => OemDetectionService.GetToolTemplate(v).ToolName)
            .ToArray();

        names.Should().OnlyHaveUniqueItems();
    }

    private static IReadOnlyDictionary<string, object?> Row(params (string Key, object? Value)[] pairs)
    {
        var d = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in pairs)
        {
            d[k] = v;
        }
        return d;
    }

    private sealed class FakeWmiRunner : IWmiQueryRunner
    {
        private readonly IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyDictionary<string, object?>>> _byMarker;

        public FakeWmiRunner(IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyDictionary<string, object?>>> byMarker)
        {
            _byMarker = byMarker;
        }

        public async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> QueryAsync(
            string scope,
            string wqlQuery,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var key = wqlQuery.Contains("Win32_ComputerSystem", StringComparison.OrdinalIgnoreCase) ? "computersystem" : "baseboard";
            if (!_byMarker.TryGetValue(key, out var rows))
            {
                yield break;
            }
            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return row;
            }
        }
    }
}
