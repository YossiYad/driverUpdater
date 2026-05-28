using System.Globalization;
using System.Text.RegularExpressions;
using DriverUpdater.Core.Models;
using HtmlAgilityPack;

namespace DriverUpdater.Infrastructure.Catalog;

public static partial class CatalogHtmlParser
{
    private const string ResultTableId = "ctl00_catalogBody_updateMatches";

    public static IReadOnlyList<CatalogSearchHit> ParseSearchResults(string html)
    {
        ArgumentNullException.ThrowIfNull(html);

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var table = doc.GetElementbyId(ResultTableId);
        if (table is null)
        {
            return Array.Empty<CatalogSearchHit>();
        }

        var rows = table.SelectNodes(".//tr[@id]");
        if (rows is null || rows.Count == 0)
        {
            return Array.Empty<CatalogSearchHit>();
        }

        var hits = new List<CatalogSearchHit>(rows.Count);
        foreach (var row in rows)
        {
            var hit = ParseRow(row);
            if (hit is not null)
            {
                hits.Add(hit);
            }
        }
        return hits;
    }

    public static IReadOnlyList<CatalogDownloadInfo> ParseDownloadDialog(string responseBody)
    {
        ArgumentNullException.ThrowIfNull(responseBody);

        var updateIds = new Dictionary<int, string>();
        foreach (Match match in DownloadIdPattern().Matches(responseBody))
        {
            var index = int.Parse(match.Groups["idx"].Value, CultureInfo.InvariantCulture);
            updateIds[index] = match.Groups["id"].Value;
        }

        var urls = new Dictionary<int, string>();
        foreach (Match match in DownloadUrlPattern().Matches(responseBody))
        {
            var index = int.Parse(match.Groups["idx"].Value, CultureInfo.InvariantCulture);
            if (!urls.ContainsKey(index))
            {
                urls[index] = match.Groups["url"].Value;
            }
        }

        var results = new List<CatalogDownloadInfo>();
        foreach (var (index, updateId) in updateIds)
        {
            if (urls.TryGetValue(index, out var url) && Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                results.Add(new CatalogDownloadInfo(updateId, uri, SizeBytes: null));
            }
        }
        return results;
    }

    public static (string ViewState, string EventValidation, string ViewStateGenerator) ParseFormTokens(string html)
    {
        ArgumentNullException.ThrowIfNull(html);

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var viewState = doc.GetElementbyId("__VIEWSTATE")?.GetAttributeValue("value", string.Empty) ?? string.Empty;
        var eventValidation = doc.GetElementbyId("__EVENTVALIDATION")?.GetAttributeValue("value", string.Empty) ?? string.Empty;
        var generator = doc.GetElementbyId("__VIEWSTATEGENERATOR")?.GetAttributeValue("value", string.Empty) ?? string.Empty;
        return (viewState, eventValidation, generator);
    }

    private static CatalogSearchHit? ParseRow(HtmlNode row)
    {
        var rowId = row.GetAttributeValue("id", string.Empty);
        if (string.IsNullOrEmpty(rowId) || !rowId.Contains("_R", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var cells = row.SelectNodes("./td");
        if (cells is null || cells.Count < 7)
        {
            return null;
        }

        var titleNode = cells[1].SelectSingleNode(".//a") ?? cells[1];
        var title = CleanText(titleNode.InnerText);

        var products = CleanText(cells[2].InnerText);
        var classification = CleanText(cells[3].InnerText);
        var lastUpdatedText = CleanText(cells[4].InnerText);
        var versionText = CleanText(cells[5].InnerText);
        var sizeText = CleanText(cells[6].InnerText);

        var downloadButton = row.SelectSingleNode(".//input[@type='button']");
        var updateId = downloadButton?.GetAttributeValue("id", string.Empty) ?? string.Empty;
        if (updateId.Length == 0)
        {
            var guidMatch = GuidPattern().Match(rowId);
            updateId = guidMatch.Success ? guidMatch.Value : rowId;
        }

        return new CatalogSearchHit(
            UpdateId: updateId,
            Title: title,
            Products: NullIfEmpty(products),
            Classification: NullIfEmpty(classification),
            LastUpdatedText: NullIfEmpty(lastUpdatedText),
            LastUpdatedDate: ParseDate(lastUpdatedText),
            VersionText: NullIfEmpty(versionText),
            Version: ParseVersion(versionText),
            SizeText: NullIfEmpty(sizeText),
            SizeBytes: ParseSize(sizeText));
    }

    private static string CleanText(string text) =>
        WhitespaceRun().Replace(HtmlEntity.DeEntitize(text).Trim(), " ");

    private static string? NullIfEmpty(string text) => string.IsNullOrWhiteSpace(text) ? null : text;

    private static DateOnly? ParseDate(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        string[] formats = ["M/d/yyyy", "MM/dd/yyyy", "yyyy-MM-dd", "d MMM yyyy"];
        return DateOnly.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
    }

    private static Version? ParseVersion(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text == "n/a")
        {
            return null;
        }
        var match = VersionPattern().Match(text);
        return match.Success && Version.TryParse(match.Value, out var version) ? version : null;
    }

    internal static long? ParseSize(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        var match = SizePattern().Match(text);
        if (!match.Success)
        {
            return null;
        }
        var value = double.Parse(match.Groups["value"].Value, CultureInfo.InvariantCulture);
        var unit = match.Groups["unit"].Value.ToUpperInvariant();
        return unit switch
        {
            "B" => (long)value,
            "KB" => (long)(value * 1024),
            "MB" => (long)(value * 1024 * 1024),
            "GB" => (long)(value * 1024 * 1024 * 1024),
            _ => null
        };
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRun();

    [GeneratedRegex(@"\b\d+\.\d+(?:\.\d+){0,2}\b")]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"(?<value>\d+(?:\.\d+)?)\s*(?<unit>B|KB|MB|GB)", RegexOptions.IgnoreCase)]
    private static partial Regex SizePattern();

    [GeneratedRegex(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex GuidPattern();

    [GeneratedRegex(@"downloadInformation\[(?<idx>\d+)\]\.updateID\s*=\s*['""](?<id>[0-9a-fA-F-]{36})['""]")]
    private static partial Regex DownloadIdPattern();

    [GeneratedRegex(@"downloadInformation\[(?<idx>\d+)\]\.files\[\d+\]\.url\s*=\s*['""](?<url>https?://[^'""]+)['""]")]
    private static partial Regex DownloadUrlPattern();
}
