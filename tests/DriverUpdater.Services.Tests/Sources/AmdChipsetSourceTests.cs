using System.Net;
using DriverUpdater.Core.Models;
using DriverUpdater.Services.Sources;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriverUpdater.Services.Tests.Sources;

public class AmdChipsetSourceTests
{
    [Fact]
    public async Task SearchAsync_yields_vendor_page_for_amd_chipset_driver()
    {
        var source = NewSource(new DateOnly(2026, 5, 28));
        var driver = NewAmdChipsetDriver("AMD I2C Controller", new DateOnly(2025, 9, 9));

        var results = await source.SearchAsync(new[] { driver }).ToListAsync();

        results.Should().ContainSingle();
        results[0].InstallKind.Should().Be(UpdateInstallKind.VendorPage);
        results[0].Confidence.Should().Be(UpdateConfidence.Advisory);
        results[0].DownloadUrl.AbsoluteUri.Should().Be("https://www.amd.com/en/support/chipsets");
        results[0].SourceUpdateId.Should().StartWith("amd-chipset-page:");
    }

    [Fact]
    public async Task SearchAsync_skips_when_local_driver_within_advisory_window()
    {
        var source = NewSource(new DateOnly(2026, 5, 28));
        var driver = NewAmdChipsetDriver("AMD I2C Controller", new DateOnly(2026, 5, 1));

        var results = await source.SearchAsync(new[] { driver }).ToListAsync();

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchAsync_skips_amd_display_drivers()
    {
        var source = NewSource(new DateOnly(2026, 5, 28));
        var displayDriver = new DriverInfo(
            DeviceId: "PCI\\VEN_1002&DEV_747E",
            HardwareId: "PCI\\VEN_1002&DEV_747E",
            DeviceName: "AMD Radeon RX 7700 XT",
            Category: DriverCategory.Display,
            Provider: "Advanced Micro Devices, Inc.",
            Manufacturer: "Advanced Micro Devices, Inc.",
            CurrentVersion: new Version(32, 0, 23027, 2005),
            CurrentDate: new DateOnly(2026, 2, 17),
            InfName: "oem69.inf",
            InfPath: null,
            IsSigned: true,
            DeviceClass: "DISPLAY");

        var results = await source.SearchAsync(new[] { displayDriver }).ToListAsync();

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchAsync_skips_microsoft_hyper_v_virtual_devices()
    {
        var source = NewSource(new DateOnly(2026, 5, 28));
        var hyperv = new DriverInfo(
            DeviceId: "ROOT\\VMBUS",
            HardwareId: "ROOT\\VMBUS",
            DeviceName: "Microsoft Hyper-V Virtual Machine Bus Provider",
            Category: DriverCategory.System,
            Provider: "Microsoft",
            Manufacturer: "Microsoft",
            CurrentVersion: new Version(10, 0, 0, 0),
            CurrentDate: new DateOnly(2025, 1, 1),
            InfName: "vmbus.inf",
            InfPath: null,
            IsSigned: true,
            DeviceClass: "SYSTEM");

        var results = await source.SearchAsync(new[] { hyperv }).ToListAsync();

        results.Should().BeEmpty();
    }

    [Fact]
    public void IsSupportedAmdChipsetDriver_matches_amd_chipset_and_system_categories()
    {
        var chipset = NewAmdChipsetDriver("AMD I2C Controller", new DateOnly(2026, 1, 1));
        var system = chipset with { Category = DriverCategory.System, DeviceName = "AMD-Vulkan User Mode Driver" };
        var display = chipset with { Category = DriverCategory.Display, DeviceName = "AMD Radeon RX 7700 XT" };
        var nonAmd = chipset with { Provider = "Intel Corporation", Manufacturer = "Intel", DeviceName = "Intel I2C Controller" };
        var virt = chipset with { DeviceName = "AMD Virtual Bus" };

        AmdChipsetSource.IsSupportedAmdChipsetDriver(chipset).Should().BeTrue();
        AmdChipsetSource.IsSupportedAmdChipsetDriver(system).Should().BeTrue();
        AmdChipsetSource.IsSupportedAmdChipsetDriver(display).Should().BeFalse();
        AmdChipsetSource.IsSupportedAmdChipsetDriver(nonAmd).Should().BeFalse();
        AmdChipsetSource.IsSupportedAmdChipsetDriver(virt).Should().BeFalse();
    }

    private static AmdChipsetSource NewSource(DateOnly today)
    {
        var client = new HttpClient(new StaticHtmlHandler()) { BaseAddress = new Uri("https://www.amd.com/") };
        var clock = new TestTimeProvider(new DateTimeOffset(today.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));
        return new AmdChipsetSource(client, NullLogger<AmdChipsetSource>.Instance, clock);
    }

    private static DriverInfo NewAmdChipsetDriver(string deviceName, DateOnly currentDate) => new(
        DeviceId: "PCI\\VEN_1022&DEV_15E2",
        HardwareId: "PCI\\VEN_1022&DEV_15E2",
        DeviceName: deviceName,
        Category: DriverCategory.Chipset,
        Provider: "Advanced Micro Devices, Inc.",
        Manufacturer: "Advanced Micro Devices, Inc.",
        CurrentVersion: new Version(1, 2, 0, 126),
        CurrentDate: currentDate,
        InfName: "oem42.inf",
        InfPath: null,
        IsSigned: true,
        DeviceClass: "SYSTEM");

    private sealed class StaticHtmlHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(string.Empty)
            });
    }

    private sealed class TestTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public TestTimeProvider(DateTimeOffset now) { _now = now; }
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
