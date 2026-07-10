using System.Globalization;
using System.Text.Json;
using DriverUpdater.Services.Sources.Internal.Motherboard;
using Microsoft.Extensions.Logging;

namespace DriverUpdater.Services.Sources.Internal.Motherboard.Asus;

// Calls ASUS's internal helpdesk download API to retrieve the driver list for a
// specific motherboard model. The endpoint was discovered via DevTools while loading
// an ASUS motherboard support page (helpdesk_download tab). It may shift between
// ASUS website revisions; when that happens GetDriversAsync throws
// ScraperUnavailableException so MotherboardSource logs it and skips ASUS updates.
public sealed class AsusMotherboardScraper : IMotherboardScraper
{
    public const string HttpClientName = "AsusScraping";

    // Undocumented ASUS internal API. LevelTagID 001002014 = "Driver" category.
    internal const string ApiEndpoint = "https://www.asus.com/support/api/product.asmx/GetPDLevel";
    internal const string DriverLevelTagId = "001002014";

    private readonly HttpClient _httpClient;
    private readonly ILogger<AsusMotherboardScraper> _logger;

    public AsusMotherboardScraper(HttpClient httpClient, ILogger<AsusMotherboardScraper> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(logger);
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<IReadOnlyList<MotherboardDriverEntry>> GetDriversAsync(
        string motherboardModel,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(motherboardModel);

        _logger.LogInformation("ASUS scraper: fetching driver list for {Model}", motherboardModel);

        var formData = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["website"] = "global",
            ["lang"] = "en",
            ["token"] = string.Empty,
            ["Model"] = motherboardModel,
            ["LevelTagID"] = DriverLevelTagId,
            ["IsLaptop"] = "0",
            ["OS"] = "Windows 11 64bit"
        });

        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, ApiEndpoint) { Content = formData };
            request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
            request.Headers.Referrer = new Uri(
                $"https://www.asus.com/support/download-center/");
            response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new ScraperUnavailableException("ASUS API request failed", ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new ScraperUnavailableException(
                $"ASUS API returned HTTP {(int)response.StatusCode} for {motherboardModel}");
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!TryParseResponse(body, out var entries))
        {
            _logger.LogWarning(
                "ASUS scraper: could not parse API response for {Model} ({Length} bytes). " +
                "The ASUS API format may have changed — inspect the raw response to update the parser.",
                motherboardModel, body.Length);
            throw new ScraperUnavailableException("ASUS API response could not be parsed");
        }

        _logger.LogInformation("ASUS scraper: parsed {Count} driver entries for {Model}", entries.Count, motherboardModel);
        return entries;
    }

    internal static bool TryParseResponse(string json, out IReadOnlyList<MotherboardDriverEntry> entries)
    {
        entries = Array.Empty<MotherboardDriverEntry>();
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // ASUS API wraps the list under "Result"; some revisions return the array directly.
            JsonElement listElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                listElement = root;
            }
            else if (root.TryGetProperty("Result", out var result) && result.ValueKind == JsonValueKind.Array)
            {
                listElement = result;
            }
            else if (root.TryGetProperty("result", out var resultLower) && resultLower.ValueKind == JsonValueKind.Array)
            {
                listElement = resultLower;
            }
            else
            {
                return false;
            }

            var parsed = new List<MotherboardDriverEntry>();
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

    private static bool TryParseEntry(JsonElement item, out MotherboardDriverEntry entry)
    {
        entry = default!;
        if (item.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var title = GetString(item, "Title", "title", "name", "Name");
        var version = GetString(item, "Version", "version");
        var dateRaw = GetString(item, "Date", "date", "ReleaseDate", "releaseDate");
        var urlRaw = GetString(item, "DownloadUrl", "downloadUrl", "URL", "url", "Link", "link");
        var sizeRaw = GetString(item, "FileSize", "fileSize", "Size", "size");
        var category = GetString(item, "Category", "category", "Type", "type") ?? "Driver";

        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(urlRaw))
        {
            return false;
        }

        if (!Uri.TryCreate(urlRaw.Trim(), UriKind.Absolute, out var downloadUrl)
            || downloadUrl.Scheme is not ("http" or "https"))
        {
            return false;
        }

        var releaseDate = TryParseDate(dateRaw, out var parsedDate) ? parsedDate : DateOnly.MinValue;

        entry = new MotherboardDriverEntry(
            Title: title.Trim(),
            Version: (version ?? string.Empty).Trim(),
            ReleaseDate: releaseDate,
            DownloadUrl: downloadUrl,
            SizeBytes: ParseSizeBytes(sizeRaw),
            Category: category.Trim());
        return true;
    }

    internal static bool TryParseDate(string? raw, out DateOnly date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        string[] formats = ["yyyy/MM/dd", "yyyy-MM-dd", "MM/dd/yyyy", "dd/MM/yyyy", "yyyy.MM.dd"];
        return DateOnly.TryParseExact(raw.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out date)
            || DateOnly.TryParse(raw.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    private static string? GetString(JsonElement element, params string[] names)
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

    private static long? ParseSizeBytes(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var match = System.Text.RegularExpressions.Regex.Match(
            raw.Trim(),
            @"(?<amount>\d+(?:\.\d+)?)\s*(?<unit>GB|MB|KB|B)?",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success || !double.TryParse(
                match.Groups["amount"].Value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var amount))
        {
            return null;
        }

        var multiplier = match.Groups["unit"].Value.ToUpperInvariant() switch
        {
            "GB" => 1024L * 1024L * 1024L,
            "MB" => 1024L * 1024L,
            "KB" => 1024L,
            "B" => 1L,
            _ => 1024L * 1024L
        };
        return (long)(amount * multiplier);
    }
}
