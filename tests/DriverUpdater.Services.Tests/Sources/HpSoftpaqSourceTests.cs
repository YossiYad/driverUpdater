using System.Xml;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using DriverUpdater.Services.Sources;
using DriverUpdater.Services.Sources.Internal.Hp;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriverUpdater.Services.Tests.Sources;

public class HpSoftpaqSourceTests
{
    private const string ReferenceXml = """
        <ImagePal>
          <Solutions>
            <UpdateInfo>
              <Id>sp140420</Id>
              <Name>Realtek High-Definition Audio Driver</Name>
              <Category>Driver-Audio</Category>
              <Version>6.0.9701.4 Rev.A</Version>
              <DateReleased>2026-05-20</DateReleased>
              <Size>52428800</Size>
              <Url>ftp.hp.com/pub/softpaq/sp140001-140500/sp140420.exe</Url>
            </UpdateInfo>
            <UpdateInfo>
              <Id>sp141000</Id>
              <Name>HP Notifications</Name>
              <Category>Software-Solutions</Category>
              <Version>2.1.0.0</Version>
              <DateReleased>2026-05-01</DateReleased>
              <Url>https://ftp.hp.com/pub/softpaq/sp141000.exe</Url>
            </UpdateInfo>
            <UpdateInfo Id="sp142222" Name="Intel Wireless LAN Driver" Category="Driver-Network" Version="23.100.0.3" DateReleased="2026-06-01" Url="ftp.hp.com/pub/softpaq/sp142222.exe" />
          </Solutions>
        </ImagePal>
        """;

    [Fact]
    public void ParseSolutions_reads_element_and_attribute_shapes()
    {
        using var reader = XmlReader.Create(new StringReader(ReferenceXml));

        var entries = HpSoftpaqParser.ParseSolutions(reader);

        entries.Should().HaveCount(3);
        entries[0].Id.Should().Be("sp140420");
        entries[0].IsDriver.Should().BeTrue();
        entries[0].Version.Should().Be(new Version(6, 0, 9701, 4));
        entries[0].ReleaseDate.Should().Be(new DateOnly(2026, 5, 20));
        entries[0].SizeBytes.Should().Be(52428800);
        entries[0].DownloadUrl.Should().Be(new Uri("https://ftp.hp.com/pub/softpaq/sp140001-140500/sp140420.exe"));
        entries[1].IsDriver.Should().BeFalse();
        entries[2].Id.Should().Be("sp142222");
        entries[2].IsDriver.Should().BeTrue();
        entries[2].Version.Should().Be(new Version(23, 100, 0, 3));
    }

    [Theory]
    [InlineData("10.0.26100", "11.0.24H2")]
    [InlineData("10.0.22631", "11.0.23H2")]
    [InlineData("10.0.19045", "10.0.22H2")]
    [InlineData("10.0.17763", null)]
    [InlineData(null, null)]
    public void BuildOsToken_maps_build_numbers(string? osVersion, string? expected)
    {
        HpSoftpaqSource.BuildOsToken(osVersion).Should().Be(expected);
    }

    [Fact]
    public async Task SearchAsync_yields_advisory_candidate_for_matching_softpaq()
    {
        var source = NewSource(OemVendor.Hp);

        var results = await CollectAsync(source.SearchAsync([NewAudioDriver(new Version(6, 0, 9000, 1))]));

        results.Should().ContainSingle();
        var candidate = results[0];
        candidate.SourceUpdateId.Should().Be("vendor-installer:hp-softpaq:sp140420");
        candidate.DownloadUrl.Host.Should().Be("ftp.hp.com");
        candidate.NewVersion.Should().Be(new Version(6, 0, 9701, 4));
        candidate.Confidence.Should().Be(UpdateConfidence.Advisory);
        candidate.InstallKind.Should().Be(UpdateInstallKind.VendorInstaller);
    }

    [Fact]
    public async Task SearchAsync_skips_when_driver_is_current()
    {
        var source = NewSource(OemVendor.Hp);

        var results = await CollectAsync(source.SearchAsync([NewAudioDriver(new Version(6, 0, 9701, 4))]));

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchAsync_skips_on_non_hp_machines()
    {
        var source = NewSource(OemVendor.Dell);

        var results = await CollectAsync(source.SearchAsync([NewAudioDriver(new Version(6, 0, 9000, 1))]));

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchAsync_does_not_match_softpaq_from_other_component_vendor()
    {
        var source = NewSource(OemVendor.Hp);
        var driver = NewAudioDriver(new Version(6, 0, 9000, 1)) with
        {
            Provider = "Conexant",
            Manufacturer = "Conexant",
            DeviceName = "Conexant ISST Audio"
        };

        var results = await CollectAsync(source.SearchAsync([driver]));

        results.Should().BeEmpty();
    }

    private static HpSoftpaqSource NewSource(OemVendor vendor)
    {
        var xmlPath = Path.Combine(Path.GetTempPath(), "DriverUpdater.Tests", Guid.NewGuid().ToString("N") + ".xml");
        Directory.CreateDirectory(Path.GetDirectoryName(xmlPath)!);
        File.WriteAllText(xmlPath, ReferenceXml);

        return new HpSoftpaqSource(
            new FakeOemDetectionService(vendor),
            new FakeRefProvider(xmlPath),
            new FakeWmiQueryRunner(),
            NullLogger<HpSoftpaqSource>.Instance);
    }

    private static DriverInfo NewAudioDriver(Version currentVersion) => new(
        DeviceId: "HDAUDIO\\X",
        HardwareId: "HDAUDIO\\FUNC_01&VEN_10EC&DEV_0299",
        DeviceName: "Realtek High Definition Audio",
        Category: DriverCategory.Audio,
        Provider: "Realtek",
        Manufacturer: "Realtek Semiconductor Corp.",
        CurrentVersion: currentVersion,
        CurrentDate: new DateOnly(2025, 1, 1),
        InfName: "oem3.inf",
        InfPath: null,
        IsSigned: true,
        DeviceClass: "MEDIA");

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
                Manufacturer: "HP",
                Model: "EliteBook 840 G8",
                ToolName: "HP Image Assistant",
                ToolPath: null,
                FallbackUrl: new Uri("https://support.hp.com")));
    }

    private sealed class FakeRefProvider : IHpSoftpaqRefProvider
    {
        private readonly string _xmlPath;

        public FakeRefProvider(string xmlPath)
        {
            _xmlPath = xmlPath;
        }

        public string? RequestedPlatform { get; private set; }
        public string? RequestedOsToken { get; private set; }

        public Task<string?> GetReferenceXmlPathAsync(string platformId, string osToken, CancellationToken cancellationToken = default)
        {
            RequestedPlatform = platformId;
            RequestedOsToken = osToken;
            return Task.FromResult<string?>(_xmlPath);
        }
    }

    private sealed class FakeWmiQueryRunner : IWmiQueryRunner
    {
        public async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> QueryAsync(
            string scope,
            string wqlQuery,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            if (wqlQuery.Contains("Win32_BaseBoard", StringComparison.OrdinalIgnoreCase))
            {
                yield return new Dictionary<string, object?> { ["Product"] = "8A14" };
            }
            else
            {
                yield return new Dictionary<string, object?> { ["Version"] = "10.0.26100" };
            }
        }
    }
}
