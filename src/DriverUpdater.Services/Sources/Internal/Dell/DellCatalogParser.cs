using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace DriverUpdater.Services.Sources.Internal.Dell;

internal sealed record DellCatalogEntry(
    string ReleaseId,
    string Name,
    string PackagePath,
    Version? VendorVersion,
    DateOnly? ReleaseDate,
    long SizeBytes,
    IReadOnlyCollection<string> SupportedSystemIds,
    IReadOnlyCollection<DellPciDevice> PciDevices)
{
    public bool AppliesToSystem(string systemId) =>
        SupportedSystemIds.Count == 0
        || SupportedSystemIds.Contains(systemId, StringComparer.OrdinalIgnoreCase);

    public bool MatchesPciDevice(string vendorId, string deviceId) =>
        PciDevices.Any(device =>
            device.VendorId.Equals(vendorId, StringComparison.OrdinalIgnoreCase)
            && device.DeviceId.Equals(deviceId, StringComparison.OrdinalIgnoreCase));
}

internal sealed record DellPciDevice(string VendorId, string DeviceId);

internal static class DellCatalogParser
{
    // Streams SoftwareComponent elements instead of loading the whole document: the
    // expanded CatalogPC.xml is on the order of 100MB and an XDocument of it would
    // multiply that in memory.
    public static List<DellCatalogEntry> ParseDriverComponents(XmlReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var entries = new List<DellCatalogEntry>();
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element
                || !reader.LocalName.Equals("SoftwareComponent", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var element = (XElement)XNode.ReadFrom(reader);
            var entry = TryParseComponent(element);
            if (entry is not null)
            {
                entries.Add(entry);
            }
        }

        return entries;
    }

    internal static DellCatalogEntry? TryParseComponent(XElement component)
    {
        var componentType = component
            .Elements().FirstOrDefault(e => e.Name.LocalName.Equals("ComponentType", StringComparison.OrdinalIgnoreCase))
            ?.Attribute("value")?.Value;
        if (!string.Equals(componentType, "DRVR", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var releaseId = component.Attribute("releaseID")?.Value;
        var packagePath = component.Attribute("path")?.Value;
        if (string.IsNullOrWhiteSpace(releaseId) || string.IsNullOrWhiteSpace(packagePath))
        {
            return null;
        }

        var pciDevices = component
            .Descendants().Where(e => e.Name.LocalName.Equals("PCIInfo", StringComparison.OrdinalIgnoreCase))
            .Select(pci => new DellPciDevice(
                pci.Attribute("vendorID")?.Value ?? string.Empty,
                pci.Attribute("deviceID")?.Value ?? string.Empty))
            .Where(device => device.VendorId.Length > 0 && device.DeviceId.Length > 0)
            .Distinct()
            .ToArray();
        if (pciDevices.Length == 0)
        {
            return null;
        }

        var systemIds = component
            .Descendants().Where(e => e.Name.LocalName.Equals("Model", StringComparison.OrdinalIgnoreCase))
            .Select(model => model.Attribute("systemID")?.Value)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var name = component
            .Elements().FirstOrDefault(e => e.Name.LocalName.Equals("Name", StringComparison.OrdinalIgnoreCase))
            ?.Elements().FirstOrDefault(e => e.Name.LocalName.Equals("Display", StringComparison.OrdinalIgnoreCase))
            ?.Value.Trim() ?? releaseId;

        return new DellCatalogEntry(
            ReleaseId: releaseId,
            Name: name,
            PackagePath: packagePath.Replace('\\', '/').TrimStart('/'),
            VendorVersion: TryParseVersion(component.Attribute("vendorVersion")?.Value),
            ReleaseDate: TryParseDate(component.Attribute("dateTime")?.Value),
            SizeBytes: long.TryParse(component.Attribute("size")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) ? size : 0,
            SupportedSystemIds: systemIds,
            PciDevices: pciDevices);
    }

    internal static Version? TryParseVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim().TrimStart('v', 'V');
        if (Version.TryParse(trimmed, out var version))
        {
            return version;
        }
        if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var major))
        {
            return new Version(major, 0);
        }
        return null;
    }

    private static DateOnly? TryParseDate(string? value)
    {
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return DateOnly.FromDateTime(parsed.UtcDateTime);
        }
        return null;
    }
}
