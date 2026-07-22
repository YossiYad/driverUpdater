using System.IO.Compression;
using System.Net;
using System.Text;
using DriverUpdater.Core.Models;
using DriverUpdater.Services.Sources;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriverUpdater.Services.Tests.Sources;

public class IntelGraphicsSourceTests
{
    private const string CatalogJson = """
        [
          {
            "Id": 100,
            "Version": "32.0.101.7000",
            "DisplayReleaseDate": "2026-01-01T00:00:00Z",
            "Name": "Intel Graphics - Windows",
            "IsBeta": false,
            "Files": [
              {
                "Url": "https://downloadmirror.intel.com/100/gfx_win_101.7000.exe",
                "Size": 500000000,
                "OperatingSystems": ["windows-11-24h2-64", "windows-10-22h2-64"]
              }
            ],
            "Components": [
              { "Category": "Graphics", "DetectionValues": ["VEN_8086&DEV_9A49"] }
            ]
          },
          {
            "Id": 101,
            "Version": "32.0.101.7088 WHQL Certified",
            "DisplayReleaseDate": "2026-06-22T00:00:00Z",
            "Name": "Intel 11th-14th Gen Processor Graphics - Windows",
            "IsBeta": false,
            "Files": [
              {
                "Url": "https://downloadmirror.intel.com/101/gfx_win_101.7088.exe",
                "Size": 600000000,
                "OperatingSystems": ["windows-11-24h2-64", "windows-10-22h2-64"]
              }
            ],
            "Components": [
              { "Category": "Graphics", "DetectionValues": ["VEN_8086&DEV_9A49&SUBSYS_00000000"] }
            ]
          },
          {
            "Id": 102,
            "Version": "99.0.0.1",
            "DisplayReleaseDate": "2026-07-01T00:00:00Z",
            "Name": "Intel Wireless Driver",
            "IsBeta": false,
            "Files": [
              {
                "Url": "https://downloadmirror.intel.com/102/wifi.exe",
                "Size": 1,
                "OperatingSystems": ["windows-11-24h2-64"]
              }
            ],
            "Components": [
              { "Category": "Wireless", "DetectionValues": ["VEN_8086&DEV_9A49"] }
            ]
          }
        ]
        """;

    [Fact]
    public void TryFindLatestRelease_matches_exact_device_and_newest_graphics_entry()
    {
        var found = IntelGraphicsSource.TryFindLatestRelease(
            CatalogJson,
            NewIntelDriver(),
            IntelGraphicsSource.IntelWindowsTarget.Windows11,
            out var release);

        found.Should().BeTrue();
        release.Id.Should().Be(101);
        release.Version.Should().Be(new Version(32, 0, 101, 7088));
        release.ReleaseDate.Should().Be(new DateOnly(2026, 6, 22));
        release.DownloadUrl.Host.Should().Be("downloadmirror.intel.com");
        release.SizeBytes.Should().Be(600000000);
    }

    [Fact]
    public void TryFindLatestRelease_rejects_wrong_device_and_wrong_os()
    {
        var wrongDevice = NewIntelDriver() with
        {
            DeviceId = "PCI\\VEN_8086&DEV_FFFF",
            HardwareId = "PCI\\VEN_8086&DEV_FFFF",
            HardwareIds = ["PCI\\VEN_8086&DEV_FFFF"]
        };
        IntelGraphicsSource.TryFindLatestRelease(
            CatalogJson,
            wrongDevice,
            IntelGraphicsSource.IntelWindowsTarget.Windows11,
            out _).Should().BeFalse();

        const string windows10Only = """
            [{
              "Id": 1,
              "Version": "31.0.101.1",
              "DisplayReleaseDate": "2026-01-01",
              "IsBeta": false,
              "Files": [{
                "Url": "https://downloadmirror.intel.com/1/driver.exe",
                "Size": 1,
                "OperatingSystems": ["windows-10-22h2-64"]
              }],
              "Components": [{"Category":"Graphics","DetectionValues":["VEN_8086&DEV_9A49"]}]
            }]
            """;
        IntelGraphicsSource.TryFindLatestRelease(
            windows10Only,
            NewIntelDriver(),
            IntelGraphicsSource.IntelWindowsTarget.Windows11,
            out _).Should().BeFalse();
    }

    [Fact]
    public async Task SearchAsync_reads_zip_catalog_and_returns_direct_installer()
    {
        var client = new HttpClient(new CatalogHandler(BuildCatalogArchive(CatalogJson)))
        {
            BaseAddress = new Uri("https://dsadata.intel.com/")
        };
        var source = new IntelGraphicsSource(client, NullLogger<IntelGraphicsSource>.Instance);

        var results = await source.SearchAsync([NewIntelDriver()]).ToListAsync();

        results.Should().ContainSingle();
        results[0].InstallKind.Should().Be(UpdateInstallKind.VendorInstaller);
        results[0].NewVersion.Should().Be(new Version(32, 0, 101, 7088));
        results[0].SourceUpdateId.Should().Be("vendor-installer:intel-graphics:101:32.0.101.7088");
        results[0].DownloadUrl.AbsoluteUri.Should().Be("https://downloadmirror.intel.com/101/gfx_win_101.7088.exe");
    }

    [Fact]
    public void TryExtractConfigurations_rejects_archive_without_configuration_entry()
    {
        var bytes = BuildCatalogArchive("{}", entryName: "other.json");

        var extracted = IntelGraphicsSource.TryExtractConfigurations(bytes, out _, out var error);

        extracted.Should().BeFalse();
        error.Should().Contain(IntelGraphicsSource.ConfigurationsEntryName);
    }

    private static byte[] BuildCatalogArchive(string json, string entryName = IntelGraphicsSource.ConfigurationsEntryName)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry(entryName);
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write(json);
        }
        return stream.ToArray();
    }

    private static DriverInfo NewIntelDriver() => new(
        DeviceId: "PCI\\VEN_8086&DEV_9A49&SUBSYS_12345678\\3&1",
        HardwareId: "PCI\\VEN_8086&DEV_9A49&SUBSYS_12345678",
        DeviceName: "Intel Iris Xe Graphics",
        Category: DriverCategory.Display,
        Provider: "Intel Corporation",
        Manufacturer: "Intel Corporation",
        CurrentVersion: new Version(31, 0, 101, 5000),
        CurrentDate: new DateOnly(2025, 1, 1),
        InfName: "iigd_dch.inf",
        InfPath: null,
        IsSigned: true,
        DeviceClass: "DISPLAY")
    {
        HardwareIds = ["PCI\\VEN_8086&DEV_9A49&SUBSYS_12345678"]
    };

    private sealed class CatalogHandler(byte[] content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content)
            });
    }
}
