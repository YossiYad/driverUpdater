using DriverUpdater.App.ViewModels;
using DriverUpdater.Core.Models;
using FluentAssertions;

namespace DriverUpdater.App.Tests.ViewModels;

public class AdvisoryLeadRetargetingTests
{
    [Fact]
    public void An_amd_component_lead_is_pointed_at_the_chipset_package_the_scan_found()
    {
        var driver = NewDriver("AMD PSP 11.0 Device", "Advanced Micro Devices, Inc.", DriverCategory.System);
        var lead = NewLead("https://www.amd.com/en/support/chipsets/amd-socket-am5/b850");
        var chipset = NewPackage(
            "vendor-installer:amd-chipset:8.08.12.551",
            "https://drivers.amd.com/drivers/AMD_Chipset_Software_8.08.12.551.exe");

        var match = AdvisoryLeadRetargeting.FindPackageForLead(driver, lead, new[] { chipset });

        match.Should().Be(chipset);

        var retargeted = AdvisoryLeadRetargeting.Retarget(lead, chipset);
        retargeted.InstallKind.Should().Be(UpdateInstallKind.VendorInstaller);
        retargeted.DownloadUrl.Should().Be(chipset.DownloadUrl);
        retargeted.SourceUpdateId.Should().Be(chipset.SourceUpdateId);
        retargeted.AiVerification.Should().Be(lead.AiVerification, "the AI reasoning still belongs to the row");
    }

    [Fact]
    public void An_amd_display_lead_is_pointed_at_the_graphics_package_not_the_chipset_one()
    {
        var driver = NewDriver("AMD Streaming Audio Device", "Advanced Micro Devices, Inc.", DriverCategory.Audio);
        var lead = NewLead("https://www.amd.com/en/technologies/noise-suppression");
        var chipset = NewPackage(
            "vendor-installer:amd-chipset:8.08.12.551",
            "https://drivers.amd.com/drivers/AMD_Chipset_Software_8.08.12.551.exe");
        var radeon = NewPackage(
            "vendor-installer:nullsoft:amd-radeon:26.7.1",
            "https://drivers.amd.com/drivers/adrenalin-26.7.1.exe");

        var match = AdvisoryLeadRetargeting.FindPackageForLead(driver, lead, new[] { chipset, radeon });

        match.Should().Be(radeon);
    }

    [Fact]
    public void A_steelseries_device_lead_is_pointed_at_the_gg_package()
    {
        var driver = NewDriver("SteelSeries GG Component Device", "SteelSeries", DriverCategory.HumanInterface);
        var lead = NewLead("https://steelseries.com/gg");
        var gg = NewPackage(
            "vendor-installer:winget:install:SteelSeries.GG:117.0.0",
            "file:///C:/winget.exe");

        AdvisoryLeadRetargeting.FindPackageForLead(driver, lead, new[] { gg }).Should().Be(gg);
    }

    [Fact]
    public void A_lead_with_no_matching_package_is_left_alone()
    {
        var driver = NewDriver("Realtek BthLeAudio", "Realtek", DriverCategory.Audio);
        var lead = NewLead("https://www.realtek.com/downloads");

        AdvisoryLeadRetargeting.FindPackageForLead(driver, lead, Array.Empty<UpdateCandidate>())
            .Should().BeNull();
    }

    [Fact]
    public void A_windows_servicing_article_is_recognised_as_having_no_package()
    {
        var lead = NewLead("https://support.microsoft.com/he-il/topic/august-11-2026-kb5121003-os-builds-26200-9168");

        AdvisoryLeadRetargeting.IsWindowsUpdateDelivered(lead).Should().BeTrue();
    }

    [Fact]
    public void A_vendor_download_page_is_not_mistaken_for_a_windows_servicing_article()
    {
        var lead = NewLead("https://www.amd.com/en/support/chipsets/amd-socket-am5/b850");

        AdvisoryLeadRetargeting.IsWindowsUpdateDelivered(lead).Should().BeFalse();
    }

    [Fact]
    public void Only_ai_page_leads_are_treated_as_advisory()
    {
        AdvisoryLeadRetargeting.IsAdvisoryPageLead(NewLead("https://steelseries.com/gg")).Should().BeTrue();
        AdvisoryLeadRetargeting.IsAdvisoryPageLead(
            NewPackage("vendor-installer:amd-chipset:8.08", "https://drivers.amd.com/x.exe")).Should().BeFalse();
        AdvisoryLeadRetargeting.IsAdvisoryPageLead(null).Should().BeFalse();
    }

    private static DriverInfo NewDriver(string name, string vendor, DriverCategory category) => new(
        DeviceId: $"ID\\{name}",
        HardwareId: $"HW\\{name}",
        DeviceName: name,
        Category: category,
        Provider: vendor,
        Manufacturer: vendor,
        CurrentVersion: new Version(1, 0, 0, 0),
        CurrentDate: new DateOnly(2024, 1, 1),
        InfName: "oem.inf",
        InfPath: null,
        IsSigned: true,
        DeviceClass: "System");

    private static UpdateCandidate NewLead(string url) => new(
        ForHardwareId: "HW\\X",
        Source: UpdateSource.Oem,
        NewVersion: new Version(2, 0, 0, 0),
        NewDate: new DateOnly(2026, 8, 1),
        DownloadUrl: new Uri(url),
        SizeBytes: 0,
        KbArticle: null,
        IsSuperseded: false,
        SourceUpdateId: "ai-latest:HW\\X",
        SupersededIds: Array.Empty<string>(),
        InstallKind: UpdateInstallKind.VendorPage,
        Confidence: UpdateConfidence.Advisory,
        AiVerification: new AiVerdict(true, AiRiskLevel.Safe, "Recommended", "Vendor ships it in the bundle", "2.0.0.0"));

    private static UpdateCandidate NewPackage(string sourceUpdateId, string url) => new(
        ForHardwareId: "HW\\Y",
        Source: UpdateSource.Oem,
        NewVersion: new Version(2, 0, 0, 0),
        NewDate: new DateOnly(2026, 8, 1),
        DownloadUrl: new Uri(url),
        SizeBytes: 1024,
        KbArticle: null,
        IsSuperseded: false,
        SourceUpdateId: sourceUpdateId,
        SupersededIds: Array.Empty<string>(),
        InstallKind: UpdateInstallKind.VendorInstaller,
        Confidence: UpdateConfidence.Confirmed);
}
