using System.Xml;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using DriverUpdater.Services.Sources;
using DriverUpdater.Services.Sources.Internal.Dell;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriverUpdater.Services.Tests.Sources;

public class DellCatalogSourceTests
{
    private const string CatalogXml = """
        <Manifest baseLocation="downloads.dell.com">
          <SoftwareComponent releaseID="8TR7T" path="FOLDER0123/network-driver.EXE" vendorVersion="22.5.0.1" dateTime="2026-06-10T10:00:00Z" size="1048576">
            <Name><Display>Intel Ethernet Driver</Display></Name>
            <ComponentType value="DRVR" />
            <SupportedDevices>
              <Device componentID="1">
                <PCIInfo vendorID="8086" deviceID="15D8" subDeviceID="0000" subVendorID="0000" />
              </Device>
            </SupportedDevices>
            <SupportedSystems>
              <Brand key="1" prefix="XPS">
                <Model systemID="0A20"><Display>XPS 15 9570</Display></Model>
              </Brand>
            </SupportedSystems>
          </SoftwareComponent>
          <SoftwareComponent releaseID="OTHER1" path="FOLDER0456/other-system.EXE" vendorVersion="30.0.0.1" dateTime="2026-06-10T10:00:00Z" size="1">
            <Name><Display>Other System Driver</Display></Name>
            <ComponentType value="DRVR" />
            <SupportedDevices>
              <Device componentID="2">
                <PCIInfo vendorID="8086" deviceID="15D8" />
              </Device>
            </SupportedDevices>
            <SupportedSystems>
              <Brand key="1" prefix="LAT">
                <Model systemID="0B99"><Display>Latitude</Display></Model>
              </Brand>
            </SupportedSystems>
          </SoftwareComponent>
          <SoftwareComponent releaseID="APP01" path="FOLDER0789/some-app.EXE" vendorVersion="99.0.0.0" dateTime="2026-06-10T10:00:00Z" size="1">
            <Name><Display>Not a driver</Display></Name>
            <ComponentType value="APAC" />
            <SupportedDevices>
              <Device componentID="3">
                <PCIInfo vendorID="8086" deviceID="15D8" />
              </Device>
            </SupportedDevices>
          </SoftwareComponent>
        </Manifest>
        """;

    [Fact]
    public void ParseDriverComponents_extracts_driver_entries_with_pci_and_systems()
    {
        using var reader = XmlReader.Create(new StringReader(CatalogXml));

        var entries = DellCatalogParser.ParseDriverComponents(reader);

        entries.Should().HaveCount(2);
        var entry = entries[0];
        entry.ReleaseId.Should().Be("8TR7T");
        entry.Name.Should().Be("Intel Ethernet Driver");
        entry.PackagePath.Should().Be("FOLDER0123/network-driver.EXE");
        entry.VendorVersion.Should().Be(new Version(22, 5, 0, 1));
        entry.ReleaseDate.Should().Be(new DateOnly(2026, 6, 10));
        entry.SizeBytes.Should().Be(1048576);
        entry.AppliesToSystem("0A20").Should().BeTrue();
        entry.AppliesToSystem("0a20").Should().BeTrue();
        entry.AppliesToSystem("0B99").Should().BeFalse();
        entry.MatchesPciDevice("8086", "15d8").Should().BeTrue();
        entry.MatchesPciDevice("8086", "9999").Should().BeFalse();
    }

    [Theory]
    [InlineData("PCI\\VEN_8086&DEV_15D8&SUBSYS_087C1028&REV_10", true, "8086", "15D8")]
    [InlineData("pci\\ven_1022&dev_43eb", true, "1022", "43eb")]
    [InlineData("USB\\VID_046D&PID_C52B", false, "", "")]
    [InlineData(null, false, "", "")]
    public void TryExtractPciIds_parses_pci_hardware_ids(string? hardwareId, bool expected, string ven, string dev)
    {
        var ok = DellCatalogSource.TryExtractPciIds(hardwareId, out var vendorId, out var deviceId);

        ok.Should().Be(expected);
        if (expected)
        {
            vendorId.Should().Be(ven);
            deviceId.Should().Be(dev);
        }
    }

    [Fact]
    public async Task SearchAsync_yields_catalog_update_for_matching_system_and_device()
    {
        var source = NewSource(OemVendor.Dell, "0A20");

        var results = await CollectAsync(source.SearchAsync([NewDriver(currentVersion: new Version(20, 0))]));

        results.Should().ContainSingle();
        var candidate = results[0];
        candidate.SourceUpdateId.Should().Be("vendor-installer:dell-dup:8TR7T");
        candidate.DownloadUrl.Should().Be(new Uri("https://downloads.dell.com/FOLDER0123/network-driver.EXE"));
        candidate.NewVersion.Should().Be(new Version(22, 5, 0, 1));
        candidate.InstallKind.Should().Be(UpdateInstallKind.VendorInstaller);
        candidate.Confidence.Should().Be(UpdateConfidence.Confirmed);
    }

    [Fact]
    public async Task SearchAsync_skips_when_driver_is_current()
    {
        var source = NewSource(OemVendor.Dell, "0A20");

        var results = await CollectAsync(source.SearchAsync([NewDriver(currentVersion: new Version(22, 5, 0, 1))]));

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchAsync_skips_on_non_dell_machines()
    {
        var source = NewSource(OemVendor.Lenovo, "0A20");

        var results = await CollectAsync(source.SearchAsync([NewDriver(currentVersion: new Version(20, 0))]));

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchAsync_skips_when_system_id_does_not_match_catalog()
    {
        var source = NewSource(OemVendor.Dell, "0C11");

        var results = await CollectAsync(source.SearchAsync([NewDriver(currentVersion: new Version(20, 0))]));

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchAsync_offers_release_once_for_multiple_matching_devices()
    {
        var source = NewSource(OemVendor.Dell, "0A20");

        var results = await CollectAsync(source.SearchAsync(
        [
            NewDriver(currentVersion: new Version(20, 0)),
            NewDriver(currentVersion: new Version(19, 0), deviceId: "PCI\\X2")
        ]));

        results.Should().ContainSingle();
    }

    private static DellCatalogSource NewSource(OemVendor vendor, string systemId)
    {
        var xmlPath = Path.Combine(Path.GetTempPath(), "DriverUpdater.Tests", Guid.NewGuid().ToString("N") + ".xml");
        Directory.CreateDirectory(Path.GetDirectoryName(xmlPath)!);
        File.WriteAllText(xmlPath, CatalogXml);

        return new DellCatalogSource(
            new FakeOemDetectionService(vendor),
            new FakeCatalogProvider(xmlPath),
            new FakeWmiQueryRunner(systemId),
            NullLogger<DellCatalogSource>.Instance);
    }

    private static DriverInfo NewDriver(Version currentVersion, string deviceId = "PCI\\X") => new(
        DeviceId: deviceId,
        HardwareId: "PCI\\VEN_8086&DEV_15D8&SUBSYS_087C1028&REV_10",
        DeviceName: "Intel Ethernet",
        Category: DriverCategory.Network,
        Provider: "Intel",
        Manufacturer: "Intel",
        CurrentVersion: currentVersion,
        CurrentDate: new DateOnly(2024, 1, 1),
        InfName: "oem7.inf",
        InfPath: null,
        IsSigned: true,
        DeviceClass: "Net");

    private static async Task<List<UpdateCandidate>> CollectAsync(IAsyncEnumerable<UpdateCandidate> candidates)
    {
        var results = new List<UpdateCandidate>();
        await foreach (var candidate in candidates)
        {
            results.Add(candidate);
        }
        return results;
    }

    private sealed class FakeOemDetectionService : IOemDetectionService
    {
        private readonly OemVendor _vendor;

        public FakeOemDetectionService(OemVendor vendor)
        {
            _vendor = vendor;
        }

        public Task<OemInfo?> DetectAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<OemInfo?>(new OemInfo(
                Vendor: _vendor,
                Manufacturer: "Dell Inc.",
                Model: "XPS 15 9570",
                ToolName: "Dell Command Update",
                ToolPath: null,
                FallbackUrl: new Uri("https://www.dell.com/support")));
    }

    private sealed class FakeCatalogProvider : IDellCatalogProvider
    {
        private readonly string _xmlPath;

        public FakeCatalogProvider(string xmlPath)
        {
            _xmlPath = xmlPath;
        }

        public Task<string?> GetCatalogXmlPathAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(_xmlPath);
    }

    private sealed class FakeWmiQueryRunner : IWmiQueryRunner
    {
        private readonly string _systemId;

        public FakeWmiQueryRunner(string systemId)
        {
            _systemId = systemId;
        }

        public async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> QueryAsync(
            string scope,
            string wqlQuery,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield return new Dictionary<string, object?> { ["SystemSKUNumber"] = _systemId };
        }
    }
}
