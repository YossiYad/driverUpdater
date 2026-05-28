using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using Microsoft.Extensions.Logging;

namespace DriverUpdater.Infrastructure.Catalog;

public sealed class CatalogHttpClient : ICatalogHttpClient
{
    public const string HttpClientName = "MicrosoftUpdateCatalog";
    private const string SearchPath = "Search.aspx";
    private const string DownloadDialogPath = "DownloadDialog.aspx";

    private readonly HttpClient _httpClient;
    private readonly ILogger<CatalogHttpClient> _logger;

    public CatalogHttpClient(HttpClient httpClient, ILogger<CatalogHttpClient> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(logger);
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<IReadOnlyList<CatalogSearchHit>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        var url = $"{SearchPath}?q={Uri.EscapeDataString(query)}";
        _logger.LogDebug("Catalog search request: {Url}", url);

        using var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return CatalogHtmlParser.ParseSearchResults(html);
    }

    public async Task<IReadOnlyList<CatalogDownloadInfo>> GetDownloadsAsync(
        IReadOnlyCollection<string> updateIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(updateIds);
        if (updateIds.Count == 0)
        {
            return Array.Empty<CatalogDownloadInfo>();
        }

        var payload = BuildUpdateIdsPayload(updateIds);
        var content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("updateIDs", payload)
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, DownloadDialogPath) { Content = content };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));

        _logger.LogDebug("Catalog download dialog request for {Count} updates", updateIds.Count);

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return CatalogHtmlParser.ParseDownloadDialog(body);
    }

    internal static string BuildUpdateIdsPayload(IReadOnlyCollection<string> updateIds)
    {
        var sb = new StringBuilder();
        sb.Append('[');
        var first = true;
        foreach (var id in updateIds)
        {
            if (!first)
            {
                sb.Append(',');
            }
            first = false;
            sb.Append('{');
            sb.Append("\"size\":0,");
            sb.Append("\"languages\":\"\",");
            sb.Append("\"uidInfo\":");
            sb.Append(JsonSerializer.Serialize(id));
            sb.Append(',');
            sb.Append("\"updateID\":");
            sb.Append(JsonSerializer.Serialize(id));
            sb.Append('}');
        }
        sb.Append(']');
        return sb.ToString();
    }
}
