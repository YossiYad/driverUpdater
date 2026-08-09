using DriverUpdater.Core.Abstractions;
using DriverUpdater.Services.Machine;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriverUpdater.Services.Tests.Machine;

public class WmiMachineProfileProviderTests
{
    [Fact]
    public async Task GetAsync_reads_the_whole_machine_from_wmi()
    {
        var provider = new WmiMachineProfileProvider(new ScriptedWmi(), NullLogger<WmiMachineProfileProvider>.Instance);

        var profile = await provider.GetAsync();

        profile.SystemManufacturer.Should().Be("ASUSTeK COMPUTER INC.");
        profile.SystemModel.Should().Be("Vivobook_ASUSLaptop X1502VA_X1502VA");
        profile.BaseBoardProduct.Should().Be("X1502VA");
        profile.BiosVersion.Should().Be("X1502VA.308");
        profile.BiosReleaseDate.Should().Be(new DateOnly(2026, 1, 14));
        profile.ProcessorName.Should().Be("13th Gen Intel(R) Core(TM) i5-13500H");
        profile.ProcessorCores.Should().Be(12);
        profile.ProcessorLogicalProcessors.Should().Be(16);
        profile.GraphicsAdapters.Should().Equal("Intel(R) Iris(R) Xe Graphics");
        profile.TotalPhysicalMemoryBytes.Should().Be(17_179_869_184);
        profile.OperatingSystemName.Should().Be("Microsoft Windows 11 Home");
        profile.OperatingSystemBuild.Should().Be("26200");
    }

    [Fact]
    public async Task GetAsync_queries_wmi_once_and_reuses_the_answer()
    {
        // Hardware identity cannot change while the app runs, and every AI prompt asks for it.
        var wmi = new ScriptedWmi();
        var provider = new WmiMachineProfileProvider(wmi, NullLogger<WmiMachineProfileProvider>.Instance);

        var first = await provider.GetAsync();
        var second = await provider.GetAsync();

        second.Should().BeSameAs(first);
        wmi.QueryCount.Should().Be(6);
    }

    [Fact]
    public async Task GetAsync_returns_what_it_could_read_when_a_query_fails()
    {
        var wmi = new ScriptedWmi { FailFor = "Win32_BIOS" };
        var provider = new WmiMachineProfileProvider(wmi, NullLogger<WmiMachineProfileProvider>.Instance);

        var profile = await provider.GetAsync();

        profile.BiosVersion.Should().BeNull();
        profile.SystemModel.Should().Be("Vivobook_ASUSLaptop X1502VA_X1502VA");
    }

    [Fact]
    public async Task GetAsync_keeps_a_second_display_adapter_and_drops_a_duplicate()
    {
        var wmi = new ScriptedWmi
        {
            VideoControllers = new[]
            {
                ("Intel(R) Iris(R) Xe Graphics", "Intel Corporation"),
                ("NVIDIA GeForce RTX 4060 Laptop GPU", "NVIDIA"),
                ("Intel(R) Iris(R) Xe Graphics", "Intel Corporation")
            }
        };
        var provider = new WmiMachineProfileProvider(wmi, NullLogger<WmiMachineProfileProvider>.Instance);

        var profile = await provider.GetAsync();

        profile.GraphicsAdapters.Should().Equal(
            "Intel(R) Iris(R) Xe Graphics",
            "NVIDIA GeForce RTX 4060 Laptop GPU");
    }

    [Fact]
    public async Task GetAsync_names_the_vendor_when_the_adapter_name_does_not()
    {
        var wmi = new ScriptedWmi { VideoControllers = new[] { ("Radeon RX 7700 XT", "Advanced Micro Devices, Inc.") } };
        var provider = new WmiMachineProfileProvider(wmi, NullLogger<WmiMachineProfileProvider>.Instance);

        var profile = await provider.GetAsync();

        profile.GraphicsAdapters.Should().Equal("Radeon RX 7700 XT (Advanced Micro Devices, Inc.)");
    }

    [Theory]
    [InlineData("20260114000000.000000+000", "2026-01-14")]
    [InlineData("20240229", "2024-02-29")]
    public void ParseCimDate_reads_the_date_half_of_a_cim_timestamp(string raw, string expected) =>
        WmiMachineProfileProvider.ParseCimDate(raw).Should().Be(DateOnly.Parse(expected));

    [Theory]
    [InlineData("")]
    [InlineData("not-a-date")]
    [InlineData("2026")]
    public void ParseCimDate_returns_null_for_anything_it_cannot_read(string raw) =>
        WmiMachineProfileProvider.ParseCimDate(raw).Should().BeNull();

    private sealed class ScriptedWmi : IWmiQueryRunner
    {
        public int QueryCount { get; private set; }

        public string? FailFor { get; init; }

        public (string Name, string Vendor)[] VideoControllers { get; init; } =
            new[] { ("Intel(R) Iris(R) Xe Graphics", "Intel Corporation") };

        public async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> QueryAsync(
            string scope,
            string wqlQuery,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            QueryCount++;
            await Task.Yield();

            if (FailFor is not null && wqlQuery.Contains(FailFor, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("WMI is unavailable for " + FailFor);
            }

            if (wqlQuery.Contains("Win32_ComputerSystem", StringComparison.Ordinal))
            {
                yield return Row(
                    ("Manufacturer", "ASUSTeK COMPUTER INC."),
                    ("Model", "Vivobook_ASUSLaptop X1502VA_X1502VA"),
                    ("SystemFamily", "Vivobook"),
                    ("SystemSKUNumber", "X1502VA"),
                    ("SystemType", "x64-based PC"),
                    ("TotalPhysicalMemory", "17179869184"));
            }
            else if (wqlQuery.Contains("Win32_BaseBoard", StringComparison.Ordinal))
            {
                yield return Row(
                    ("Manufacturer", "ASUSTeK COMPUTER INC."),
                    ("Product", "X1502VA"),
                    ("Version", "1.0"));
            }
            else if (wqlQuery.Contains("Win32_BIOS", StringComparison.Ordinal))
            {
                yield return Row(
                    ("Manufacturer", "American Megatrends"),
                    ("SMBIOSBIOSVersion", "X1502VA.308"),
                    ("ReleaseDate", "20260114000000.000000+000"));
            }
            else if (wqlQuery.Contains("Win32_Processor", StringComparison.Ordinal))
            {
                yield return Row(
                    ("Name", "13th Gen Intel(R) Core(TM) i5-13500H"),
                    ("NumberOfCores", "12"),
                    ("NumberOfLogicalProcessors", "16"));
            }
            else if (wqlQuery.Contains("Win32_VideoController", StringComparison.Ordinal))
            {
                foreach (var (name, vendor) in VideoControllers)
                {
                    yield return Row(("Name", name), ("AdapterCompatibility", vendor));
                }
            }
            else if (wqlQuery.Contains("Win32_OperatingSystem", StringComparison.Ordinal))
            {
                yield return Row(
                    ("Caption", "Microsoft Windows 11 Home"),
                    ("Version", "10.0.26200"),
                    ("BuildNumber", "26200"),
                    ("OSArchitecture", "64-bit"));
            }
        }

        private static IReadOnlyDictionary<string, object?> Row(params (string Key, object? Value)[] values) =>
            values.ToDictionary(v => v.Key, v => v.Value, StringComparer.OrdinalIgnoreCase);
    }
}
