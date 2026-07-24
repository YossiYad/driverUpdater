using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using DriverUpdater.Core.Options;
using DriverUpdater.Services;
using DriverUpdater.Services.Ai;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DriverUpdater.App.Tests.Integration;

// End-to-end checks that exercise the real DI container the app composes at startup
// (AddDriverUpdaterServices + Configure<AiSettings>) plus, for Ollama, a real HTTP
// round-trip over a loopback socket through the real named HttpClient.
public class AiCompositionIntegrationTests
{
    [Fact]
    public void Container_resolves_IAiVerifier_as_selector_and_is_off_by_default()
    {
        using var provider = BuildProvider(new AiSettings());

        var verifier = provider.GetRequiredService<IAiVerifier>();

        verifier.Should().BeOfType<AiVerifierSelector>();
        verifier.Provider.Should().Be(AiProvider.Off);
        verifier.IsConfigured.Should().BeFalse();
    }

    [Fact]
    public void Container_reports_gemini_unconfigured_without_a_key()
    {
        using var provider = BuildProvider(new AiSettings { Provider = AiProvider.Gemini, GeminiApiKey = "" });

        var verifier = provider.GetRequiredService<IAiVerifier>();

        verifier.Provider.Should().Be(AiProvider.Gemini);
        verifier.IsConfigured.Should().BeFalse();
    }

    [Fact]
    public void Container_registers_the_named_ai_http_client()
    {
        using var provider = BuildProvider(new AiSettings());

        var factory = provider.GetRequiredService<IHttpClientFactory>();
        var client = factory.CreateClient(GeminiAiVerifier.HttpClientName);

        client.Should().NotBeNull();
        client.Timeout.Should().Be(Timeout.InfiniteTimeSpan);
    }

    [Fact]
    public async Task EndToEnd_ollama_over_a_real_socket_returns_parsed_verdicts()
    {
        using var stub = new LoopbackHttpStub(OllamaBody(id: "row-1", genuinelyNewer: false, risk: "Caution"));
        using var provider = BuildProvider(new AiSettings
        {
            Provider = AiProvider.Ollama,
            OllamaBaseUrl = stub.BaseUrl,
            OllamaModel = "llama3.1"
        });

        var verifier = provider.GetRequiredService<IAiVerifier>();
        verifier.IsConfigured.Should().BeTrue();

        var verdicts = await verifier.VerifyAsync(new[] { NewRequest("row-1") });

        verdicts.Should().ContainKey("row-1");
        verdicts["row-1"].IsGenuinelyNewer.Should().BeFalse();
        verdicts["row-1"].Risk.Should().Be(AiRiskLevel.Caution);
        stub.ReceivedRequests.Should().BeGreaterThan(0);
        stub.LastRequestTarget.Should().EndWith("/api/chat");
    }

    private static ServiceProvider BuildProvider(AiSettings ai)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.Configure<AiSettings>(o =>
        {
            o.Provider = ai.Provider;
            o.GeminiApiKey = ai.GeminiApiKey;
            o.GeminiApiKeys = ai.GeminiApiKeys.ToList();
            o.GeminiModel = ai.GeminiModel;
            o.EnableWebSearch = ai.EnableWebSearch;
            o.OllamaBaseUrl = ai.OllamaBaseUrl;
            o.OllamaModel = ai.OllamaModel;
        });
        services.AddDriverUpdaterServices();
        return services.BuildServiceProvider();
    }

    private static string OllamaBody(string id, bool genuinelyNewer, string risk)
    {
        var modelText = "{\"verdicts\":[{\"id\":\"" + id + "\",\"isGenuinelyNewer\":"
            + (genuinelyNewer ? "true" : "false")
            + ",\"risk\":\"" + risk + "\",\"summary\":\"ok\",\"rationale\":\"because\",\"latestKnownVersion\":null}]}";
        var escaped = JsonSerializer.Serialize(modelText);
        return "{\"message\":{\"role\":\"assistant\",\"content\":" + escaped + "}}";
    }

    private static AiVerificationRequest NewRequest(string correlationId) => new(
        CorrelationId: correlationId,
        DeviceName: "Realtek Audio",
        HardwareId: "PCI\\VEN_10EC&DEV_8168",
        InstalledVersion: "6.0.9927.1",
        InstalledDate: new DateOnly(2024, 1, 1),
        CandidateVersion: "6.0.9927.1",
        CandidateDate: new DateOnly(2026, 1, 1),
        Source: UpdateSource.Oem,
        DownloadUrl: "https://example.com/audio.exe");

    private sealed class LoopbackHttpStub : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly string _responseBody;
        private int _received;

        public LoopbackHttpStub(string responseBody)
        {
            _responseBody = responseBody;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            BaseUrl = $"http://127.0.0.1:{port}";
            _ = AcceptLoopAsync(_cts.Token);
        }

        public string BaseUrl { get; }
        public int ReceivedRequests => _received;
        public string? LastRequestTarget { get; private set; }

        private async Task AcceptLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
                }
                catch
                {
                    return;
                }

                using (client)
                using (var stream = client.GetStream())
                {
                    try
                    {
                        await HandleAsync(stream, token).ConfigureAwait(false);
                    }
                    catch
                    {
                        // best-effort stub; ignore broken connections
                    }
                }
            }
        }

        private async Task HandleAsync(NetworkStream stream, CancellationToken token)
        {
            var buffer = new byte[16384];
            using var ms = new MemoryStream();
            var contentLength = -1;
            var headerEnd = -1;
            var isChunked = false;

            while (true)
            {
                var read = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }
                ms.Write(buffer, 0, read);
                var text = Encoding.ASCII.GetString(ms.GetBuffer(), 0, (int)ms.Length);

                if (headerEnd < 0)
                {
                    var idx = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                    if (idx >= 0)
                    {
                        headerEnd = idx;
                        LastRequestTarget = ParseRequestTarget(text);
                        contentLength = ParseContentLength(text);
                        isChunked = IsChunked(text);
                    }
                }

                if (headerEnd >= 0)
                {
                    var bodyBytes = ms.Length - (headerEnd + 4);
                    if (contentLength >= 0 && bodyBytes >= contentLength)
                    {
                        break;
                    }
                    if (isChunked && text.Contains("\r\n0\r\n\r\n", StringComparison.Ordinal))
                    {
                        break;
                    }
                }
            }

            _received++;
            var bodyBytesOut = Encoding.UTF8.GetBytes(_responseBody);
            var head = "HTTP/1.1 200 OK\r\n"
                + "Content-Type: application/json\r\n"
                + $"Content-Length: {bodyBytesOut.Length}\r\n"
                + "Connection: close\r\n\r\n";
            var headBytes = Encoding.ASCII.GetBytes(head);
            await stream.WriteAsync(headBytes, token).ConfigureAwait(false);
            await stream.WriteAsync(bodyBytesOut, token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false);
        }

        private static string? ParseRequestTarget(string headerText)
        {
            var firstLine = headerText.Split("\r\n", StringSplitOptions.None)[0];
            var parts = firstLine.Split(' ');
            return parts.Length >= 2 ? parts[1] : null;
        }

        private static int ParseContentLength(string headerText)
        {
            foreach (var line in headerText.Split("\r\n", StringSplitOptions.None))
            {
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(line.Split(':', 2)[1].Trim(), out var len))
                {
                    return len;
                }
            }
            return -1;
        }

        private static bool IsChunked(string headerText)
        {
            foreach (var line in headerText.Split("\r\n", StringSplitOptions.None))
            {
                if (line.StartsWith("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase)
                    && line.Contains("chunked", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            _cts.Dispose();
        }
    }
}
