using System.Net.Http;
using System.Net;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using Microsoft.Extensions.Options;

namespace DriverUpdater.EndToEnd.Tests.Harness;

/// <summary>An <see cref="IOptionsMonitor{T}"/> over a fixed value, for wiring real services.</summary>
public sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
{
    public StaticOptionsMonitor(T value) => CurrentValue = value;

    public T CurrentValue { get; private set; }

    public T Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<T, string?> listener) => null;

    public void Set(T value) => CurrentValue = value;
}

/// <summary>Serves a fixed byte payload for every download the pipeline performs.</summary>
public sealed class StubHttpClientFactory : IHttpClientFactory
{
    private readonly byte[] _payload;
    private readonly string _contentType;

    public StubHttpClientFactory(byte[] payload, string contentType = "application/octet-stream")
    {
        _payload = payload;
        _contentType = contentType;
    }

    public List<Uri> RequestedUris { get; } = new();

    public HttpClient CreateClient(string name) => new(new Handler(this));

    private sealed class Handler : HttpMessageHandler
    {
        private readonly StubHttpClientFactory _owner;

        public Handler(StubHttpClientFactory owner) => _owner = owner;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _owner.RequestedUris.Add(request.RequestUri!);
            var content = new ByteArrayContent(_owner._payload);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(_owner._contentType);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}

/// <summary>
/// Emulates the one thing the pipeline uses PowerShell for: running expand.exe over a downloaded
/// .cab. It parses the destination out of the generated script and materialises an INF there,
/// exactly as the real expand.exe would.
/// </summary>
public sealed class FakeExpandPowerShellInvoker : IPowerShellInvoker
{
    public List<string> Scripts { get; } = new();

    public bool ProduceInfFiles { get; set; } = true;

    public Task<ProcessResult> InvokeAsync(string script, CancellationToken cancellationToken = default)
    {
        Scripts.Add(script);

        var literals = ExtractSingleQuotedLiterals(script);
        if (ProduceInfFiles && literals.Count >= 3)
        {
            var destination = literals[^1];
            System.IO.Directory.CreateDirectory(destination);
            System.IO.File.WriteAllText(
                System.IO.Path.Combine(destination, "expanded-driver.inf"),
                "[Version]\r\nSignature=\"$WINDOWS NT$\"\r\n");
        }

        return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
    }

    private static List<string> ExtractSingleQuotedLiterals(string script)
    {
        var literals = new List<string>();
        var index = 0;
        while (index < script.Length)
        {
            var open = script.IndexOf('\'', index);
            if (open < 0)
            {
                break;
            }
            var close = script.IndexOf('\'', open + 1);
            if (close < 0)
            {
                break;
            }
            literals.Add(script[(open + 1)..close]);
            index = close + 1;
        }
        return literals;
    }
}

/// <summary>A manually advanced clock, so time-dependent gates can be driven deterministically.</summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _now;

    public ManualTimeProvider(DateTimeOffset start) => _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan amount) => _now += amount;
}
