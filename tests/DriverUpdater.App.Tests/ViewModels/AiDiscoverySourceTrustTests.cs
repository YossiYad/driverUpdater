using DriverUpdater.App.ViewModels;
using DriverUpdater.Core.Models;
using FluentAssertions;

namespace DriverUpdater.App.Tests.ViewModels;

public class AiDiscoverySourceTrustTests
{
    [Theory]
    [InlineData("https://www.amd.com/en/support/chipsets/amd-socket-am5/b850")]
    [InlineData("https://drivers.amd.com/drivers/AMD_Chipset_Software_8.08.12.551.exe")]
    [InlineData("https://support.microsoft.com/he-il/topic/august-11-2026-kb5121003")]
    [InlineData("https://github.com/nefarius/ViGEmBus/releases")]
    public void A_page_the_vendor_publishes_on_is_accepted(string url)
    {
        var device = NewDriver("Advanced Micro Devices, Inc.");

        AiDiscoverySourceTrust.IsPublishedByTheVendor(device, new Uri(url)).Should().BeTrue();
    }

    [Theory]
    [InlineData("https://www.techspot.com/downloads/6764-steelseries-gg.html")]
    [InlineData("https://www.treexy.com/drivers/device/logitech-lightspeed-receiver")]
    [InlineData("https://www.driverscloud.com/en/drivers/amd")]
    [InlineData("https://download.cnet.com/amd-chipset-driver/3000-2094_4-10065000.html")]
    public void A_third_party_download_portal_is_refused(string url)
    {
        var device = NewDriver("Advanced Micro Devices, Inc.");

        AiDiscoverySourceTrust.IsPublishedByTheVendor(device, new Uri(url)).Should().BeFalse();
    }

    [Fact]
    public void A_look_alike_domain_does_not_pass_for_containing_the_vendor_name()
    {
        var device = NewDriver("Advanced Micro Devices, Inc.");

        AiDiscoverySourceTrust
            .IsPublishedByTheVendor(device, new Uri("https://amd-driver-download.com/latest.exe"))
            .Should().BeFalse();
    }

    [Fact]
    public void A_vendor_not_on_the_list_is_recognised_from_the_name_the_device_reports()
    {
        var device = NewDriver("Realtek Semiconductor Corp.");

        AiDiscoverySourceTrust
            .IsPublishedByTheVendor(device, new Uri("https://www.realtek.com/downloads/"))
            .Should().BeTrue();
    }

    [Fact]
    public void A_generic_manufacturer_name_cannot_vouch_for_a_domain()
    {
        var device = NewDriver("(Standard system devices)");

        AiDiscoverySourceTrust
            .IsPublishedByTheVendor(device, new Uri("https://system.com/drivers"))
            .Should().BeFalse();
        AiDiscoverySourceTrust
            .IsPublishedByTheVendor(device, new Uri("https://devices.com/drivers"))
            .Should().BeFalse();
    }

    [Fact]
    public void A_non_web_url_is_refused()
    {
        var device = NewDriver("Advanced Micro Devices, Inc.");

        AiDiscoverySourceTrust
            .IsPublishedByTheVendor(device, new Uri("file:///C:/Temp/driver.exe"))
            .Should().BeFalse();
    }

    private static DriverInfo NewDriver(string vendor) => new(
        DeviceId: "ID\\Device",
        HardwareId: "PCI\\VEN_1002&DEV_747E",
        DeviceName: "Test device",
        Category: DriverCategory.System,
        Provider: vendor,
        Manufacturer: vendor,
        CurrentVersion: new Version(1, 0, 0, 0),
        CurrentDate: new DateOnly(2024, 1, 1),
        InfName: "oem.inf",
        InfPath: null,
        IsSigned: true,
        DeviceClass: "System");
}
