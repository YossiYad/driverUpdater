using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DriverUpdater.Services.Sources.Internal.Lenovo;

internal sealed record LenovoPackageRef(Uri DescriptorUrl, string Category);

internal sealed record LenovoPackage(
    string Id,
    string Name,
    Version? Version,
    DateOnly? ReleaseDate,
    Uri InstallerUrl,
    long SizeBytes,
    IReadOnlyCollection<string> PnpIds)
{
    public bool MatchesPciDevice(string vendorId, string deviceId)
    {
        var needle = $"VEN_{vendorId}&DEV_{deviceId}";
        return PnpIds.Any(id => id.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }
}

internal static partial class LenovoCatalogParser
{
    public static List<LenovoPackageRef> ParseCatalog(string catalogXml)
    {
        ArgumentNullException.ThrowIfNull(catalogXml);

        var document = XDocument.Parse(catalogXml);
        var refs = new List<LenovoPackageRef>();
        foreach (var package in document.Descendants().Where(e => e.Name.LocalName.Equals("package", StringComparison.OrdinalIgnoreCase)))
        {
            var location = package.Elements()
                .FirstOrDefault(e => e.Name.LocalName.Equals("location", StringComparison.OrdinalIgnoreCase))
                ?.Value.Trim();
            if (string.IsNullOrWhiteSpace(location)
                || !Uri.TryCreate(location, UriKind.Absolute, out var descriptorUrl))
            {
                continue;
            }

            var category = package.Elements()
                .FirstOrDefault(e => e.Name.LocalName.Equals("category", StringComparison.OrdinalIgnoreCase))
                ?.Value.Trim() ?? string.Empty;
            refs.Add(new LenovoPackageRef(descriptorUrl, category));
        }

        return refs;
    }

    // Lenovo package descriptors have varied their element layout across generations;
    // the PnP IDs are pulled with a regex over the raw XML so a schema drift cannot
    // silently drop the hardware match.
    public static LenovoPackage? ParsePackageDescriptor(string descriptorXml, Uri descriptorUrl)
    {
        ArgumentNullException.ThrowIfNull(descriptorXml);
        ArgumentNullException.ThrowIfNull(descriptorUrl);

        XDocument document;
        try
        {
            document = XDocument.Parse(descriptorXml);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }

        var root = document.Root;
        if (root is null)
        {
            return null;
        }

        var id = GetAttribute(root, "id") ?? GetAttribute(root, "name");
        if (string.IsNullOrWhiteSpace(id))
        {
            id = Path.GetFileNameWithoutExtension(descriptorUrl.LocalPath);
        }

        var installerFile = document.Descendants()
            .Where(e => e.Name.LocalName.Equals("File", StringComparison.OrdinalIgnoreCase))
            .Select(file => new
            {
                Name = file.Elements().FirstOrDefault(e => e.Name.LocalName.Equals("Name", StringComparison.OrdinalIgnoreCase))?.Value.Trim(),
                Size = file.Elements().FirstOrDefault(e => e.Name.LocalName.Equals("Size", StringComparison.OrdinalIgnoreCase))?.Value.Trim()
            })
            .FirstOrDefault(file => file.Name?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true);
        if (installerFile?.Name is null)
        {
            return null;
        }

        var baseUrl = descriptorUrl.AbsoluteUri[..(descriptorUrl.AbsoluteUri.LastIndexOf('/') + 1)];
        if (!Uri.TryCreate(baseUrl + installerFile.Name, UriKind.Absolute, out var installerUrl))
        {
            return null;
        }

        var pnpIds = PnpIdPattern().Matches(descriptorXml.Replace("&amp;", "&", StringComparison.OrdinalIgnoreCase))
            .Select(match => match.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var name = document.Descendants()
            .FirstOrDefault(e => e.Name.LocalName.Equals("Title", StringComparison.OrdinalIgnoreCase))
            ?.Descendants()
            .Select(e => e.Value.Trim())
            .FirstOrDefault(value => value.Length > 0);

        return new LenovoPackage(
            Id: id.Trim(),
            Name: string.IsNullOrWhiteSpace(name) ? id.Trim() : name,
            Version: TryParseVersion(GetAttribute(root, "version")),
            ReleaseDate: TryParseDate(
                document.Descendants().FirstOrDefault(e => e.Name.LocalName.Equals("ReleaseDate", StringComparison.OrdinalIgnoreCase))?.Value),
            InstallerUrl: installerUrl,
            SizeBytes: long.TryParse(installerFile.Size, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) ? size : 0,
            PnpIds: pnpIds);
    }

    internal static Version? TryParseVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var token = value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        return Version.TryParse(token, out var version) ? version : null;
    }

    private static DateOnly? TryParseDate(string? value)
    {
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return DateOnly.FromDateTime(parsed.UtcDateTime);
        }
        return null;
    }

    private static string? GetAttribute(XElement element, string name) =>
        element.Attributes()
            .FirstOrDefault(a => a.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?.Value;

    [GeneratedRegex(@"PCI\\VEN_[0-9A-F]{4}&DEV_[0-9A-F]{4}[0-9A-Z&_]*", RegexOptions.IgnoreCase)]
    private static partial Regex PnpIdPattern();
}
