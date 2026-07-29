using System.Net;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using DriverUpdater.Services.Sources;
using DriverUpdater.Services.Sources.Internal.Lenovo;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriverUpdater.Services.Tests.Sources;

public class LenovoCatalogSourceTests
{
    private const string CatalogXml = """
        <packages>
          <package>
            <category>Networking: Wireless LAN</category>
            <location>https://download.lenovo.com/pccbbs/mobiles/wlan_driver_2_.xml</location>
          </package>
          <package>
            <category>BIOS UEFI</category>
            <location>https://download.lenovo.com/pccbbs/mobiles/bios_2_.xml</location>
          </package>
          <package>
            <category>Software and Utilities</category>
            <location>https://download.lenovo.com/pccbbs/mobiles/util_2_.xml</location>
          </package>
        </packages>
        """;

    private const string PackageXml = """
        <Package id="wlan_driver" name="wlan_driver" version="23.100.0.5">
          <Title default="EN"><Desc id="EN">Intel Wireless LAN Driver</Desc></Title>
          <ReleaseDate>2026-06-15</ReleaseDate>
          <DetectInstall>
            <And>
              <_PnPID><HardwareID>PCI\VEN_8086&amp;DEV_51F1&amp;SUBSYS_00908086</HardwareID></_PnPID>
            </And>
          </DetectInstall>
          <Files>
            <Installer>
              <File>
                <Name>wlan_driver.exe</Name>
                <Size>12345678</Size>
              </File>
            </Installer>
          </Files>
        </Package>
        """;

    [Fact]
    public void ParseCatalog_returns_driver_package_refs_with_categories()
    {
        var refs = LenovoCatalogParser.ParseCatalog(CatalogXml);

        refs.Should().HaveCount(3);
        refs[0].Category.Should().Be("Networking: Wireless LAN");
        refs[0].DescriptorUrl.Should().Be(new Uri("https://download.lenovo.com/pccbbs/mobiles/wlan_driver_2_.xml"));
    }

    [Fact]
    public void ParsePackageDescriptor_extracts_version_installer_and_pnp_ids()
    {
        var package = LenovoCatalogParser.ParsePackageDescriptor(
            PackageXml, new Uri("https://download.lenovo.com/pccbbs/mobiles/wlan_driver_2_.xml"));

        package.Should().NotBeNull();
        package!.Id.Should().Be("wlan_driver");
        package.Name.Should().Be("Intel Wireless LAN Driver");
        package.Version.Should().Be(new Version(23, 100, 0, 5));
        package.ReleaseDate.Should().Be(new DateOnly(2026, 6, 15));
        package.InstallerUrl.Should().Be(new Uri("https://download.lenovo.com/pccbbs/mobiles/wlan_driver.exe"));
        package.SizeBytes.Should().Be(12345678);
        package.MatchesPciDevice("8086", "51F1").Should().BeTrue();
        package.MatchesPciDevice("8086", "0000").Should().BeFalse();
    }

    [Theory]
    [InlineData("21F8CTO1WW", "21F8")]
    [InlineData("20xw004qge", "20XW")]
    [InlineData("X1", null)]
    [InlineData(null, null)]
    public void ExtractMachineType_takes_first_four_model_characters(string? model, string? expected)
    {
        LenovoCatalogSource.ExtractMachineType(model).Should().Be(expected);
    }

    [Theory]
    [InlineData("Networking: Wireless LAN", true)]
    [InlineData("Display and Video Graphics", true)]
    [InlineData("BIOS UEFI", false)]
    [InlineData("Software and Utilities", false)]
    public void IsDriverCategory_filters_driver_categories(string category, bool expected)
    {
        LenovoCatalogSource.IsDriverCategory(category).Should().Be(expected);
    }

    [Fact]
    public async Task SearchAsync_yields_confirmed_candidate_for_matching_pnp_id()
    {
        var source = NewSource(OemVendor.Lenovo, out var handler);

        var results = await CollectAsync(source.SearchAsync([NewWlanDriver(new Version(22, 0, 0, 1))]));

        results.Should().ContainSingle();
        var candidate = results[0];
        candidate.SourceUpdateId.Should().Be("vendor-installer:lenovo-catalog:wlan_driver");
        candidate.DownloadUrl.Should().Be(new Uri("https://download.lenovo.com/pccbbs/mobiles/wlan_driver.exe"));
        candidate.NewVersion.Should().Be(new Version(23, 100, 0, 5));
        candidate.Confidence.Should().Be(UpdateConfidence.Confirmed);
        handler.RequestedUrls.Should().Contain(url => url.EndsWith("/catalog/21f8_Win11.xml"));
        handler.RequestedUrls.Should().NotContain(url => url.Contains("bios"));
        handler.RequestedUrls.Should().NotContain(url => url.Contains("util"));
    }

    [Fact]
    public async Task SearchAsync_skips_when_driver_is_current()
    {
        var source = NewSource(OemVendor.Lenovo, out _);

        var results = await CollectAsync(source.SearchAsync([NewWlanDriver(new Version(23, 100, 0, 5))]));

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchAsync_skips_on_non_lenovo_machines()
    {
        var source = NewSource(OemVendor.Hp, out var handler);

        var results = await CollectAsync(source.SearchAsync([NewWlanDriver(new Version(22, 0, 0, 1))]));

        results.Should().BeEmpty();
        handler.RequestedUrls.Should().BeEmpty();
    }

    private static LenovoCatalogSource NewSource(OemVendor vendor, out RecordingHandler handler)
    {
        handler = new RecordingHandler(url =>
        {
            if (url.EndsWith("/catalog/21f8_Win11.xml", StringComparison.OrdinalIgnoreCase))
            {
                return CatalogXml;
            }
            if (url.EndsWith("wlan_driver_2_.xml", StringComparison.OrdinalIgnoreCase))
            {
                return PackageXml;
            }
            return null;
        });

        return new LenovoCatalogSource(
            new HttpClient(handler),
            new FakeOemDetectionService(vendor),
            new FakeWmiQueryRunner(),
            NullLogger<LenovoCatalogSource>.Instance);
    }

    private static DriverInfo NewWlanDriver(Version currentVersion) => new(
        DeviceId: "PCI\\WLAN",
        HardwareId: "PCI\\VEN_8086&DEV_51F1&SUBSYS_00908086&REV_01",
        DeviceName: "Intel Wi-Fi 6E AX211",
        Category: DriverCategory.Network,
        Provider: "Intel",
        Manufacturer: "Intel Corporation",
        CurrentVersion: currentVersion,
        CurrentDate: new DateOnly(2025, 1, 1),
        InfName: "oem9.inf",
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
                Manufacturer: "LENOVO",
                Model: "21F8CTO1WW",
                ToolName: "Lenovo System Update",
                ToolPath: null,
                FallbackUrl: new Uri("https://support.lenovo.com")));
    }

    private sealed class FakeWmiQueryRunner : IWmiQueryRunner
    {
        public async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> QueryAsync(
            string scope,
            string wqlQuery,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield return new Dictionary<string, object?> { ["Version"] = "10.0.26100" };
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<string, string?> _responder;

        public RecordingHandler(Func<string, string?> responder)
        {
            _responder = responder;
        }

        public List<string> RequestedUrls { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            RequestedUrls.Add(url);
            var body = _responder(url);
            if (body is null)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body)
            });
        }
    }
}
