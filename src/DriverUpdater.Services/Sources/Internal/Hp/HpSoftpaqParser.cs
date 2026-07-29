using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace DriverUpdater.Services.Sources.Internal.Hp;

internal sealed record HpSoftpaqEntry(
    string Id,
    string Name,
    string Category,
    Version? Version,
    DateOnly? ReleaseDate,
    long SizeBytes,
    Uri DownloadUrl)
{
    public bool IsDriver => Category.StartsWith("Driver", StringComparison.OrdinalIgnoreCase);
}

internal static class HpSoftpaqParser
{
    // HPIA reference files vary between putting UpdateInfo fields in attributes and in
    // child elements across platform generations, so both shapes are read.
    public static List<HpSoftpaqEntry> ParseSolutions(XmlReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var entries = new List<HpSoftpaqEntry>();
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element
                || !reader.LocalName.Equals("UpdateInfo", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var element = (XElement)XNode.ReadFrom(reader);
            var entry = TryParseUpdateInfo(element);
            if (entry is not null)
            {
                entries.Add(entry);
            }
        }

        return entries;
    }

    internal static HpSoftpaqEntry? TryParseUpdateInfo(XElement updateInfo)
    {
        var id = GetField(updateInfo, "Id");
        var url = GetField(updateInfo, "Url") ?? GetField(updateInfo, "URL");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var downloadUrl = NormalizeUrl(url);
        if (downloadUrl is null)
        {
            return null;
        }

        return new HpSoftpaqEntry(
            Id: id.Trim(),
            Name: GetField(updateInfo, "Name")?.Trim() ?? id.Trim(),
            Category: GetField(updateInfo, "Category")?.Trim() ?? string.Empty,
            Version: TryParseVersion(GetField(updateInfo, "Version")),
            ReleaseDate: TryParseDate(GetField(updateInfo, "DateReleased") ?? GetField(updateInfo, "ReleaseDate")),
            SizeBytes: long.TryParse(GetField(updateInfo, "Size"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) ? size : 0,
            DownloadUrl: downloadUrl);
    }

    internal static Uri? NormalizeUrl(string url)
    {
        var trimmed = url.Trim();
        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return Uri.TryCreate(trimmed, UriKind.Absolute, out var absolute) ? absolute : null;
        }
        if (trimmed.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed["ftp://".Length..];
        }

        return Uri.TryCreate("https://" + trimmed, UriKind.Absolute, out var normalized) ? normalized : null;
    }

    internal static Version? TryParseVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        // HP versions carry suffixes like "6.0.9701.4 Rev.A"; the leading numeric part is
        // the comparable driver version.
        var token = value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        if (Version.TryParse(token, out var version))
        {
            return version;
        }
        if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var major))
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

    private static string? GetField(XElement element, string name)
    {
        var attribute = element.Attributes()
            .FirstOrDefault(a => a.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?.Value;
        if (!string.IsNullOrWhiteSpace(attribute))
        {
            return attribute;
        }

        var child = element.Elements()
            .FirstOrDefault(e => e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?.Value;
        return string.IsNullOrWhiteSpace(child) ? null : child;
    }
}
