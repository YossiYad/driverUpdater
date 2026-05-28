using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace DriverUpdater.Services.Sources.Internal.Gigabyte;

// Attempts the lightweight path: call Gigabyte's internal driver-search endpoint with
// browser-mimicking headers. The endpoint is undocumented and discovered via DevTools
// Network tab while loading a motherboard support page. It may shift or be blocked by
// Akamai - when that happens GetDriversAsync throws ScraperUnavailableException so the
// hybrid wrapper can fall through to Playwright (or to an empty result if Playwright is
// disabled).
public sealed class GigabyteApiScraper : IGigabyteScraper
{
    public const string HttpClientName = "GigabyteScraping";

    // Best-known list endpoint. If Gigabyte rotates this, the API path lives in the
    // SPA's bundled JS - the user needs to recapture it from DevTools and update this
    // constant.
    internal const string DriverListPath = "/Ajax/Support_BIOSDriver_New";

    private readonly HttpClient _httpClient;
    private readonly ILogger<GigabyteApiScraper> _logger;

    public GigabyteApiScraper(HttpClient httpClient, ILogger<GigabyteApiScraper> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(logger);
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<IReadOnlyList<GigabyteDriverEntry>> GetDriversAsync(string motherboardModel, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(motherboardModel);

        var normalized = NormalizeModel(motherboardModel);
        var requestUri = new Uri($"{DriverListPath}?productid={Uri.EscapeDataString(normalized)}&os=Win11x64&type=driver", UriKind.Relative);
        _logger.LogInformation("GigabyteApi: fetching {Uri} for {Model}", requestUri, normalized);

        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            request.Headers.TryAddWithoutValidation("Accept", "application/json,text/plain,*/*");
            request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
            request.Headers.Referrer = new Uri($"https://www.gigabyte.com/Motherboard/{Uri.EscapeDataString(normalized)}/support");
            response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new ScraperUnavailableException("Gigabyte API request failed", ex);
        }

        if (response.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.BadRequest)
        {
            throw new ScraperUnavailableException($"Gigabyte API returned {(int)response.StatusCode}");
        }

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!TryParseDriverList(body, out var entries))
        {
            throw new ScraperUnavailableException("Gigabyte API response could not be parsed");
        }

        _logger.LogInformation("GigabyteApi: parsed {Count} driver entries for {Model}", entries.Count, normalized);
        return entries;
    }

    internal static string NormalizeModel(string model)
    {
        // Gigabyte URL slugs collapse rev numbers by dropping the dot: "rev. 1.0" -> "rev-10",
        // "rev. 1.2" -> "rev-12". Spaces become hyphens. Verified live against
        // https://www.gigabyte.com/Motherboard/B850M-GAMING-X-WIFI6E-rev-10/support.
        var trimmed = model.Trim();
        var withoutRev = System.Text.RegularExpressions.Regex.Replace(
            trimmed,
            @"\s*\(rev\.?\s+(?<digits>[0-9.]+)\)\s*",
            m => "-rev-" + m.Groups["digits"].Value.Replace(".", "", StringComparison.Ordinal),
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return System.Text.RegularExpressions.Regex.Replace(withoutRev, @"\s+", "-");
    }

    internal static bool TryParseDriverList(string json, out IReadOnlyList<GigabyteDriverEntry> entries)
    {
        entries = Array.Empty<GigabyteDriverEntry>();
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            JsonElement listElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                listElement = root;
            }
            else if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                listElement = data;
            }
            else if (root.TryGetProperty("Drivers", out var drivers) && drivers.ValueKind == JsonValueKind.Array)
            {
                listElement = drivers;
            }
            else
            {
                return false;
            }

            var parsed = new List<GigabyteDriverEntry>();
            foreach (var item in listElement.EnumerateArray())
            {
                if (TryParseEntry(item, out var entry))
                {
                    parsed.Add(entry);
                }
            }

            entries = parsed;
            return parsed.Count > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryParseEntry(JsonElement item, out GigabyteDriverEntry entry)
    {
        entry = default!;
        if (item.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var title = GetStringProperty(item, "title", "Title", "name", "Name");
        var version = GetStringProperty(item, "version", "Version");
        var dateRaw = GetStringProperty(item, "date", "Date", "releaseDate", "ReleaseDate");
        var urlRaw = GetStringProperty(item, "downloadUrl", "DownloadUrl", "url", "Url", "link", "Link");
        var sizeRaw = GetStringProperty(item, "size", "Size", "fileSize", "FileSize");
        var category = GetStringProperty(item, "category", "Category", "type", "Type") ?? "Driver";

        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(urlRaw))
        {
            return false;
        }

        if (!Uri.TryCreate(urlRaw, UriKind.Absolute, out var downloadUrl) || downloadUrl.Scheme is not ("http" or "https"))
        {
            return false;
        }

        var releaseDate = TryParseDate(dateRaw, out var parsedDate) ? parsedDate : DateOnly.MinValue;

        entry = new GigabyteDriverEntry(title.Trim(), version.Trim(), releaseDate, downloadUrl, ParseSizeBytes(sizeRaw), category.Trim());
        return true;
    }

    private static string? GetStringProperty(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String)
            {
                return prop.GetString();
            }
        }
        return null;
    }

    internal static bool TryParseDate(string? raw, out DateOnly date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        string[] formats =
        [
            "yyyy-MM-dd", "yyyy/MM/dd", "MM/dd/yyyy", "dd/MM/yyyy", "yyyy.MM.dd"
        ];
        return DateOnly.TryParseExact(raw.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out date)
            || DateOnly.TryParse(raw.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    private static long? ParseSizeBytes(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var trimmed = raw.Trim();
        var match = System.Text.RegularExpressions.Regex.Match(trimmed, @"(?<amount>\d+(?:\.\d+)?)\s*(?<unit>GB|MB|KB|B)?", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success || !double.TryParse(match.Groups["amount"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount))
        {
            return null;
        }

        var multiplier = match.Groups["unit"].Value.ToUpperInvariant() switch
        {
            "GB" => 1024L * 1024L * 1024L,
            "MB" => 1024L * 1024L,
            "KB" => 1024L,
            "B" => 1L,
            _ => 1024L * 1024L // default MB
        };
        return (long)(amount * multiplier);
    }
}
