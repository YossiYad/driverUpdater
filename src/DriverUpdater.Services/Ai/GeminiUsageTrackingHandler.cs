namespace DriverUpdater.Services.Ai;

public sealed class GeminiUsageTrackingHandler : DelegatingHandler
{
    private const string GeminiHost = "generativelanguage.googleapis.com";
    private readonly GeminiRequestUsageTracker _usageTracker;

    public GeminiUsageTrackingHandler(GeminiRequestUsageTracker usageTracker)
    {
        ArgumentNullException.ThrowIfNull(usageTracker);
        _usageTracker = usageTracker;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (TryGetGeminiModel(request.RequestUri, out var model))
        {
            _usageTracker.RecordRequest(model);
        }

        return base.SendAsync(request, cancellationToken);
    }

    internal static bool TryGetGeminiModel(Uri? requestUri, out string model)
    {
        model = string.Empty;
        if (requestUri is null
            || !requestUri.Host.Equals(GeminiHost, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        const string marker = "/models/";
        var path = requestUri.AbsolutePath;
        var markerIndex = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return false;
        }

        var modelStart = markerIndex + marker.Length;
        var methodSeparator = path.IndexOf(':', modelStart);
        if (methodSeparator <= modelStart)
        {
            return false;
        }

        model = Uri.UnescapeDataString(path[modelStart..methodSeparator]);
        return !string.IsNullOrWhiteSpace(model);
    }
}
