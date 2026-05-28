using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using DriverUpdater.Services.Sources;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriverUpdater.Services.Tests.Sources;

public class OemSupportSourceTests
{
    [Fact]
    public async Task SearchAsync_yields_oem_vendor_page_for_old_motherboard_driver()
    {
        var source = new OemSupportSource(
            new FakeOemDetectionService(new OemInfo(
                OemVendor.Gigabyte,
                "Gigabyte Technology Co., Ltd.",
                "Z790 AORUS ELITE AX",
                "GIGABYTE Control Center",
                ToolPath: null,
                FallbackUrl: new Uri("https://www.gigabyte.com/Search?kw=Z790%20AORUS%20ELITE%20AX"))),
            NullLogger<OemSupportSource>.Instance,
            new FakeTimeProvider(new DateTimeOffset(2026, 5, 28, 0, 0, 0, TimeSpan.Zero)));

        var results = await source.SearchAsync(new[]
        {
            NewDriver("ASMedia USB 3.2 Host Controller", DriverCategory.Usb, "ASMedia Technology Inc.", new DateOnly(2024, 1, 1)),
            NewDriver("AMD Radeon RX 7700 XT", DriverCategory.Display, "Advanced Micro Devices", new DateOnly(2024, 1, 1)),
            NewDriver("AMD SMBus", DriverCategory.Chipset, "Advanced Micro Devices", new DateOnly(2024, 1, 1))
        }).ToListAsync();

        results.Should().ContainSingle();
        results[0].InstallKind.Should().Be(UpdateInstallKind.VendorPage);
        results[0].DownloadUrl.Host.Should().Contain("gigabyte.com");
    }

    [Fact]
    public async Task SearchAsync_skips_drivers_with_dedicated_component_vendor()
    {
        var source = new OemSupportSource(
            new FakeOemDetectionService(new OemInfo(
                OemVendor.Gigabyte,
                "Gigabyte Technology Co., Ltd.",
                "B850M GAMING X WIFI6E",
                "GIGABYTE Control Center",
                ToolPath: null,
                FallbackUrl: new Uri("https://www.gigabyte.com/Search?kw=B850M"))),
            NullLogger<OemSupportSource>.Instance,
            new FakeTimeProvider(new DateTimeOffset(2026, 5, 28, 0, 0, 0, TimeSpan.Zero)));

        var results = await source.SearchAsync(new[]
        {
            NewDriver("AMD SMBus", DriverCategory.Chipset, "Advanced Micro Devices", new DateOnly(2024, 1, 1)),
            NewDriver("Realtek PCIe 2.5GbE Family Controller", DriverCategory.Network, "Realtek", new DateOnly(2024, 1, 1)),
            NewDriver("LIGHTSPEED Receiver", DriverCategory.Usb, "Logitech", new DateOnly(2024, 1, 1)),
            NewDriver("Intel Wi-Fi 7", DriverCategory.Network, "Intel Corporation", new DateOnly(2024, 1, 1))
        }).ToListAsync();

        results.Should().BeEmpty();
    }

    [Theory]
    [InlineData(DriverCategory.Chipset, true)]
    [InlineData(DriverCategory.Network, true)]
    [InlineData(DriverCategory.Audio, true)]
    [InlineData(DriverCategory.Display, false)]
    [InlineData(DriverCategory.Printer, false)]
    public void IsMotherboardDriverCandidate_filters_expected_categories(DriverCategory category, bool expected)
    {
        OemSupportSource.IsMotherboardDriverCandidate(NewDriver("Device", category, "Vendor", new DateOnly(2024, 1, 1)))
            .Should().Be(expected);
    }

    private static DriverInfo NewDriver(string deviceName, DriverCategory category, string provider, DateOnly currentDate) => new(
        DeviceId: $"ID\\{deviceName}",
        HardwareId: $"HW\\{deviceName}",
        DeviceName: deviceName,
        Category: category,
        Provider: provider,
        Manufacturer: provider,
        CurrentVersion: new Version(1, 0, 0, 0),
        CurrentDate: currentDate,
        InfName: "oem.inf",
        InfPath: null,
        IsSigned: true,
        DeviceClass: category.ToString());

    private sealed class FakeOemDetectionService : IOemDetectionService
    {
        private readonly OemInfo? _info;

        public FakeOemDetectionService(OemInfo? info)
        {
            _info = info;
        }

        public Task<OemInfo?> DetectAsync(CancellationToken cancellationToken = default) => Task.FromResult(_info);
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FakeTimeProvider(DateTimeOffset now)
        {
            _now = now;
        }

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
