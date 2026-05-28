using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using Microsoft.Extensions.Logging;

namespace DriverUpdater.Services.Sources;

public sealed class OemDetectionService : IOemDetectionService
{
    private const string CimV2Scope = "\\\\.\\root\\CIMV2";
    private const string ComputerSystemQuery = "SELECT Manufacturer, Model FROM Win32_ComputerSystem";
    private const string BaseBoardQuery = "SELECT Manufacturer, Product FROM Win32_BaseBoard";

    private readonly IWmiQueryRunner _wmi;
    private readonly ILogger<OemDetectionService> _logger;

    public OemDetectionService(IWmiQueryRunner wmi, ILogger<OemDetectionService> logger)
    {
        ArgumentNullException.ThrowIfNull(wmi);
        ArgumentNullException.ThrowIfNull(logger);
        _wmi = wmi;
        _logger = logger;
    }

    public async Task<OemInfo?> DetectAsync(CancellationToken cancellationToken = default)
    {
        string? csManufacturer = null;
        string? csModel = null;
        await foreach (var row in _wmi.QueryAsync(CimV2Scope, ComputerSystemQuery, cancellationToken).ConfigureAwait(false))
        {
            csManufacturer = row.TryGetValue("Manufacturer", out var m) ? m?.ToString() : null;
            csModel = row.TryGetValue("Model", out var v) ? v?.ToString() : null;
            break;
        }

        if (string.IsNullOrWhiteSpace(csManufacturer))
        {
            await foreach (var row in _wmi.QueryAsync(CimV2Scope, BaseBoardQuery, cancellationToken).ConfigureAwait(false))
            {
                csManufacturer = row.TryGetValue("Manufacturer", out var m) ? m?.ToString() : null;
                csModel ??= row.TryGetValue("Product", out var v) ? v?.ToString() : null;
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(csManufacturer))
        {
            _logger.LogDebug("Could not read manufacturer from WMI");
            return null;
        }

        var vendor = MapVendor(csManufacturer, csModel ?? string.Empty);
        if (vendor == OemVendor.Unknown)
        {
            _logger.LogInformation("OEM not recognized (manufacturer={Manufacturer})", csManufacturer);
            return null;
        }

        var template = GetToolTemplate(vendor);
        var toolPath = ResolveToolPath(template.CandidateToolPaths);
        var fallbackUrl = ResolveVendorSupportUrl(vendor, csModel) ?? template.FallbackUrl;

        var info = new OemInfo(
            Vendor: vendor,
            Manufacturer: csManufacturer.Trim(),
            Model: (csModel ?? string.Empty).Trim(),
            ToolName: template.ToolName,
            ToolPath: toolPath,
            FallbackUrl: fallbackUrl);

        _logger.LogInformation("OEM detected: {Vendor} {Model} (tool installed={Installed})", vendor, info.Model, info.ToolInstalled);
        return info;
    }

    internal static OemVendor MapVendor(string manufacturer, string model)
    {
        var lookup = (manufacturer + " " + model).ToUpperInvariant();
        if (Contains(lookup, "LENOVO"))
        {
            return OemVendor.Lenovo;
        }
        if (Contains(lookup, "DELL"))
        {
            return OemVendor.Dell;
        }
        if (Contains(lookup, "HEWLETT-PACKARD") || Contains(lookup, "HEWLETT PACKARD") || Contains(lookup, "HP "))
        {
            return OemVendor.Hp;
        }
        if (Contains(lookup, "MICRO-STAR") || Contains(lookup, "MSI "))
        {
            return OemVendor.Msi;
        }
        if (Contains(lookup, "ASUSTEK") || Contains(lookup, "ASUS ") || Contains(lookup, "ASUS_"))
        {
            return OemVendor.Asus;
        }
        if (Contains(lookup, "ACER"))
        {
            return OemVendor.Acer;
        }
        if (Contains(lookup, "MICROSOFT CORPORATION") && Contains(lookup, "SURFACE"))
        {
            return OemVendor.MicrosoftSurface;
        }
        if (Contains(lookup, "RAZER"))
        {
            return OemVendor.Razer;
        }
        if (Contains(lookup, "SAMSUNG"))
        {
            return OemVendor.Samsung;
        }
        if (Contains(lookup, "TOSHIBA"))
        {
            return OemVendor.Toshiba;
        }
        if (Contains(lookup, "GIGABYTE"))
        {
            return OemVendor.Gigabyte;
        }
        if (Contains(lookup, "ASROCK"))
        {
            return OemVendor.ASRock;
        }
        if (Contains(lookup, "BIOSTAR"))
        {
            return OemVendor.Biostar;
        }
        return OemVendor.Unknown;
    }

    private static bool Contains(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.Ordinal);

    private static string? ResolveToolPath(IEnumerable<string> candidates)
    {
        foreach (var path in candidates)
        {
            try
            {
                if (File.Exists(path))
                {
                    return path;
                }
            }
            catch
            {
            }
        }
        return null;
    }

    internal static OemToolTemplate GetToolTemplate(OemVendor vendor)
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        return vendor switch
        {
            OemVendor.Lenovo => new OemToolTemplate(
                ToolName: "Lenovo System Update",
                CandidateToolPaths: new[]
                {
                    Path.Combine(programFilesX86, "Lenovo", "System Update", "tvsu.exe"),
                    Path.Combine(programFiles, "Lenovo", "System Update", "tvsu.exe"),
                    Path.Combine(programFiles, "Lenovo", "Vantage", "Vantage.exe"),
                    Path.Combine(programFilesX86, "Lenovo", "VantageService", "LenovoVantageService.exe")
                },
                FallbackUrl: new Uri("https://support.lenovo.com/downloads/")),
            OemVendor.Dell => new OemToolTemplate(
                ToolName: "Dell Command Update",
                CandidateToolPaths: new[]
                {
                    Path.Combine(programFiles, "Dell", "CommandUpdate", "dcu-cli.exe"),
                    Path.Combine(programFilesX86, "Dell", "CommandUpdate", "dcu-cli.exe"),
                    Path.Combine(programFiles, "Dell", "CommandUpdate", "DellCommandUpdate.exe")
                },
                FallbackUrl: new Uri("https://www.dell.com/support/home/drivers/driversdetails")),
            OemVendor.Hp => new OemToolTemplate(
                ToolName: "HP Image Assistant",
                CandidateToolPaths: new[]
                {
                    Path.Combine(programFiles, "HP", "HP Image Assistant", "HPImageAssistant.exe"),
                    Path.Combine(programFilesX86, "HP", "HP Image Assistant", "HPImageAssistant.exe"),
                    Path.Combine(programFilesX86, "HP", "HP Support Framework", "HPSF.exe"),
                    Path.Combine(programFilesX86, "HP", "HP Support Solutions", "HPSF.exe")
                },
                FallbackUrl: new Uri("https://support.hp.com/drivers")),
            OemVendor.Msi => new OemToolTemplate(
                ToolName: "MSI Center",
                CandidateToolPaths: new[]
                {
                    Path.Combine(programFiles, "MSI", "MSI Center", "MSI.CentralServer.exe")
                },
                FallbackUrl: new Uri("https://www.msi.com/support")),
            OemVendor.Asus => new OemToolTemplate(
                ToolName: "MyASUS",
                CandidateToolPaths: new[]
                {
                    Path.Combine(programFiles, "ASUS", "ARMOURY CRATE Service", "ArmouryCrate.exe"),
                    Path.Combine(programFiles, "ASUS", "MyASUS", "MyASUS.exe")
                },
                FallbackUrl: new Uri("https://www.asus.com/support/download-center/")),
            OemVendor.Acer => new OemToolTemplate(
                ToolName: "Acer Care Center",
                CandidateToolPaths: new[]
                {
                    Path.Combine(programFiles, "Acer", "Acer Care Center", "ACenter.exe")
                },
                FallbackUrl: new Uri("https://www.acer.com/us-en/support/drivers-and-manuals")),
            OemVendor.MicrosoftSurface => new OemToolTemplate(
                ToolName: "Surface Diagnostic Toolkit",
                CandidateToolPaths: Array.Empty<string>(),
                FallbackUrl: new Uri("https://support.microsoft.com/surface")),
            OemVendor.Razer => new OemToolTemplate(
                ToolName: "Razer Synapse",
                CandidateToolPaths: new[]
                {
                    Path.Combine(programFilesX86, "Razer", "Synapse3", "WPFUI", "Framework", "Razer Synapse 3 Host", "Razer Synapse 3.exe")
                },
                FallbackUrl: new Uri("https://www.razer.com/support")),
            OemVendor.Samsung => new OemToolTemplate(
                ToolName: "Samsung Update",
                CandidateToolPaths: new[]
                {
                    Path.Combine(programFiles, "Samsung", "Samsung Update", "SamsungUpdate.exe")
                },
                FallbackUrl: new Uri("https://www.samsung.com/us/support/downloads/")),
            OemVendor.Toshiba => new OemToolTemplate(
                ToolName: "Toshiba Service Station",
                CandidateToolPaths: Array.Empty<string>(),
                FallbackUrl: new Uri("https://support.dynabook.com/")),
            OemVendor.Gigabyte => new OemToolTemplate(
                ToolName: "GIGABYTE Control Center",
                CandidateToolPaths: new[]
                {
                    Path.Combine(programFiles, "GIGABYTE", "Control Center", "GCC.exe"),
                    Path.Combine(programFilesX86, "GIGABYTE", "Control Center", "GCC.exe")
                },
                FallbackUrl: new Uri("https://www.gigabyte.com/Support")),
            OemVendor.ASRock => new OemToolTemplate(
                ToolName: "ASRock APP Shop",
                CandidateToolPaths: Array.Empty<string>(),
                FallbackUrl: new Uri("https://www.asrock.com/support/index.asp")),
            OemVendor.Biostar => new OemToolTemplate(
                ToolName: "BIOSTAR support",
                CandidateToolPaths: Array.Empty<string>(),
                FallbackUrl: new Uri("https://www.biostar.com.tw/app/en/support/download.php")),
            _ => new OemToolTemplate(
                ToolName: "OEM tool",
                CandidateToolPaths: Array.Empty<string>(),
                FallbackUrl: new Uri("https://support.microsoft.com"))
        };
    }

    internal static Uri? ResolveVendorSupportUrl(OemVendor vendor, string? model)
    {
        var query = NormalizeModelQuery(model);
        return vendor switch
        {
            OemVendor.Lenovo when query is not null => new Uri($"https://support.lenovo.com/search?query={Uri.EscapeDataString(query)}"),
            OemVendor.Dell when query is not null => new Uri($"https://www.dell.com/support/search/en-us#q={Uri.EscapeDataString(query)}"),
            OemVendor.Hp when query is not null => new Uri($"https://support.hp.com/us-en/search?q={Uri.EscapeDataString(query)}"),
            OemVendor.Msi when query is not null => new Uri($"https://www.msi.com/search/{Uri.EscapeDataString(query)}"),
            OemVendor.Asus when query is not null => new Uri($"https://www.asus.com/support/search-result/?keyword={Uri.EscapeDataString(query)}"),
            OemVendor.Acer when query is not null => new Uri($"https://www.acer.com/us-en/search?q={Uri.EscapeDataString(query)}"),
            OemVendor.Razer when query is not null => new Uri($"https://mysupport.razer.com/app/answers/list/kw/{Uri.EscapeDataString(query)}"),
            OemVendor.Samsung when query is not null => new Uri($"https://www.samsung.com/us/search/searchMain/?searchTerm={Uri.EscapeDataString(query)}"),
            OemVendor.Gigabyte when query is not null => new Uri($"https://www.gigabyte.com/Search?kw={Uri.EscapeDataString(query)}"),
            OemVendor.ASRock when query is not null => new Uri($"https://www.asrock.com/search/index.asp?q={Uri.EscapeDataString(query)}"),
            OemVendor.Biostar when query is not null => new Uri($"https://www.biostar.com.tw/app/en/search/search.php?keyword={Uri.EscapeDataString(query)}"),
            _ => null
        };
    }

    internal static string? NormalizeModelQuery(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        var normalized = string.Join(' ', model.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return normalized.Length == 0 ? null : normalized;
    }

    internal sealed record OemToolTemplate(
        string ToolName,
        IReadOnlyList<string> CandidateToolPaths,
        Uri FallbackUrl);
}
