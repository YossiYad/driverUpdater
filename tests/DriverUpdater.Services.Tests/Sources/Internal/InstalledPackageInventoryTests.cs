using DriverUpdater.Services.Sources.Internal;
using FluentAssertions;

namespace DriverUpdater.Services.Tests.Sources.Internal;

public class InstalledPackageInventoryTests
{
    [Theory]
    [InlineData("26.6.2", "26.6.2", true)]
    [InlineData("26.7.1 WHQL", "26.6.2", true)]
    [InlineData("26.5.1", "26.6.2", false)]
    [InlineData("8.05.04.516", "8.05.04.516", true)]
    [InlineData(null, "26.6.2", false)]
    public void IsInstalledPackageCurrent_normalizes_vendor_versions(
        string? installed,
        string release,
        bool expected)
    {
        InstalledPackageInventory.IsInstalledPackageCurrent(installed, release).Should().Be(expected);
    }

    [Fact]
    public void FindRadeonPackageVersion_is_independent_of_registry_key_name_and_view()
    {
        InstalledPackage[] packages =
        [
            new("AMD Software", "26.5.1", "Advanced Micro Devices, Inc.", "HKLM/64/random-guid"),
            new("AMD Software: Adrenalin Edition", "26.6.2", "Advanced Micro Devices, Inc.", "HKLM/32/another-guid"),
            new("Unrelated Software", "99.0", "Other Publisher", "HKCU/64/foo")
        ];

        AmdInstalledPackageDetector.FindRadeonPackageVersion(packages).Should().Be("26.6.2");
    }

    [Fact]
    public void FindChipsetPackageVersion_matches_localized_key_with_product_metadata()
    {
        InstalledPackage[] packages =
        [
            new("AMD Chipset Software", "8.05.04.516", "Advanced Micro Devices, Inc.", "arbitrary-key"),
            new("AMD Ryzen Master", "99.0", "Advanced Micro Devices, Inc.", "other-key")
        ];

        AmdInstalledPackageDetector.FindChipsetPackageVersion(packages).Should().Be("8.05.04.516");
    }

    [Fact]
    public void Amd_match_rejects_similarly_named_product_from_another_publisher()
    {
        InstalledPackage[] packages =
        [
            new("AMD Software Helper", "99.0", "Unrelated Vendor", "fake")
        ];

        AmdInstalledPackageDetector.FindRadeonPackageVersion(packages).Should().BeNull();
    }
}
