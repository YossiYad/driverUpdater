using System.Runtime.CompilerServices;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using DriverUpdater.Services.Sources;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriverUpdater.Services.Tests.Sources;

public class WindowsUpdateSourceTests
{
    [Fact]
    public async Task SearchAsync_maps_records_to_update_candidates()
    {
        var records = new[]
        {
            new WuDriverUpdateRecord(
                UpdateId: "1111-2222",
                RevisionNumber: 1,
                Title: "Intel Corporation - Display - 30.0.101.4502",
                DriverHardwareId: "PCI\\VEN_8086&DEV_4682",
                DriverModel: "Intel UHD Graphics",
                DriverManufacturer: "Intel",
                DriverProvider: "Intel",
                DriverVerDate: new DateOnly(2024, 3, 6),
                MaxDownloadSize: 1234567,
                DownloadUrl: "https://download.windowsupdate.com/test.cab",
                KbArticleIds: new[] { "5012345" }),
        };

        var source = NewSource(records);
        var candidates = new List<UpdateCandidate>();
        await foreach (var c in source.SearchAsync(Array.Empty<DriverInfo>()))
        {
            candidates.Add(c);
        }

        candidates.Should().ContainSingle();
        var candidate = candidates[0];
        candidate.ForHardwareId.Should().Be("PCI\\VEN_8086&DEV_4682");
        candidate.Source.Should().Be(UpdateSource.WindowsUpdate);
        candidate.NewVersion.Should().Be(new Version(30, 0, 101, 4502));
        candidate.NewDate.Should().Be(new DateOnly(2024, 3, 6));
        candidate.DownloadUrl.Should().Be(new Uri("https://download.windowsupdate.com/test.cab"));
        candidate.SizeBytes.Should().Be(1234567);
        candidate.KbArticle.Should().Be("KB5012345");
        candidate.SourceUpdateId.Should().Be("1111-2222");
    }

    [Fact]
    public async Task SearchAsync_skips_records_with_empty_update_id()
    {
        var records = new[]
        {
            new WuDriverUpdateRecord(
                UpdateId: "",
                RevisionNumber: 0,
                Title: "Broken update",
                DriverHardwareId: null,
                DriverModel: null,
                DriverManufacturer: null,
                DriverProvider: null,
                DriverVerDate: null,
                MaxDownloadSize: 0,
                DownloadUrl: null,
                KbArticleIds: Array.Empty<string>())
        };

        var source = NewSource(records);
        var candidates = new List<UpdateCandidate>();
        await foreach (var c in source.SearchAsync(Array.Empty<DriverInfo>()))
        {
            candidates.Add(c);
        }

        candidates.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchAsync_uses_date_based_version_when_title_lacks_version()
    {
        var records = new[]
        {
            new WuDriverUpdateRecord(
                UpdateId: "abc",
                RevisionNumber: 1,
                Title: "Generic Driver Update",
                DriverHardwareId: "USB\\VID_046D&PID_0000",
                DriverModel: null,
                DriverManufacturer: null,
                DriverProvider: null,
                DriverVerDate: new DateOnly(2025, 6, 15),
                MaxDownloadSize: 1000,
                DownloadUrl: "https://example.com/x.cab",
                KbArticleIds: Array.Empty<string>())
        };

        var source = NewSource(records);
        var candidates = new List<UpdateCandidate>();
        await foreach (var c in source.SearchAsync(Array.Empty<DriverInfo>()))
        {
            candidates.Add(c);
        }

        candidates.Should().ContainSingle();
        candidates[0].NewVersion.Should().Be(new Version(2025, 6, 15, 0));
        candidates[0].KbArticle.Should().BeNull();
    }

    [Theory]
    [InlineData("Intel - Display - 30.0.101.4502", "30.0.101.4502")]
    [InlineData("NVIDIA - 552.22", "552.22")]
    [InlineData("Driver 1.0", "1.0")]
    [InlineData("No version here", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ExtractVersionFromTitle_pulls_first_dotted_number(string? title, string? expected)
    {
        var version = WindowsUpdateSource.ExtractVersionFromTitle(title);
        version?.ToString().Should().Be(expected);
    }

    [Fact]
    public void DateToVersion_uses_year_month_day()
    {
        WindowsUpdateSource.DateToVersion(new DateOnly(2024, 3, 6))
            .Should().Be(new Version(2024, 3, 6, 0));
    }

    [Fact]
    public void DateToVersion_returns_null_for_null_date()
    {
        WindowsUpdateSource.DateToVersion(null).Should().BeNull();
    }

    private static WindowsUpdateSource NewSource(IEnumerable<WuDriverUpdateRecord> records) =>
        new(new FakeWuApiClient(records), NullLogger<WindowsUpdateSource>.Instance);

    private sealed class FakeWuApiClient : IWuApiClient
    {
        private readonly IEnumerable<WuDriverUpdateRecord> _records;

        public FakeWuApiClient(IEnumerable<WuDriverUpdateRecord> records)
        {
            _records = records;
        }

        public async IAsyncEnumerable<WuDriverUpdateRecord> SearchDriverUpdatesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var record in _records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return record;
            }
        }

        public Task<DriverUpdater.Core.Results.Result<WuInstallResult>> DownloadAndInstallAsync(
            string updateId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
