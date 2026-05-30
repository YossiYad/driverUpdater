using System.Net;
using System.Text.Json;
using DriverUpdater.Core.Options;
using Microsoft.Extensions.Options;

namespace DriverUpdater.Services.Tests.Ai;

internal static class AiResponses
{
    // The text a model would emit: a JSON object with the verdicts array.
    public static string VerdictsText(string id, bool genuinelyNewer, string risk, string? latest = "2.0.0.0")
    {
        var latestJson = latest is null ? "null" : "\"" + latest + "\"";
        return "{\"verdicts\":[{\"id\":\"" + id + "\",\"isGenuinelyNewer\":"
            + (genuinelyNewer ? "true" : "false")
            + ",\"risk\":\"" + risk + "\",\"summary\":\"ok\",\"rationale\":\"because\",\"latestKnownVersion\":"
            + latestJson + "}]}";
    }

    // Wraps the model text in the Gemini generateContent response envelope.
    public static string Gemini(string modelText)
    {
        var escaped = JsonSerializer.Serialize(modelText);
        return "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":" + escaped + "}]}}]}";
    }

    // Wraps the model text in the Ollama /api/chat response envelope.
    public static string Ollama(string modelText)
    {
        var escaped = JsonSerializer.Serialize(modelText);
        return "{\"message\":{\"role\":\"assistant\",\"content\":" + escaped + "}}";
    }
}

internal sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
{
    public StaticOptionsMonitor(T value) => CurrentValue = value;

    public T CurrentValue { get; }

    public T Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

internal sealed class SingleClientHttpClientFactory : IHttpClientFactory
{
    private readonly HttpMessageHandler _handler;

    public SingleClientHttpClientFactory(HttpMessageHandler handler) => _handler = handler;

    public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
}

internal sealed class CapturingHandler : HttpMessageHandler
{
    private readonly HttpStatusCode _status;
    private readonly string _responseBody;

    public CapturingHandler(string responseBody, HttpStatusCode status = HttpStatusCode.OK)
    {
        _responseBody = responseBody;
        _status = status;
    }

    public HttpRequestMessage? LastRequest { get; private set; }
    public string? LastRequestBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        if (request.Content is not null)
        {
            LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        return new HttpResponseMessage(_status)
        {
            Content = new StringContent(_responseBody)
        };
    }
}

internal sealed class ThrowingHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken) =>
        throw new HttpRequestException("network down");
}

internal static class AiTestSettings
{
    public static StaticOptionsMonitor<AiSettings> Monitor(AiSettings settings) => new(settings);
}
