using DriverUpdater.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace DriverUpdater.Services.Sources.Internal.Hp;

public interface IHpSoftpaqRefProvider
{
    Task<string?> GetReferenceXmlPathAsync(string platformId, string osToken, CancellationToken cancellationToken = default);
}

public sealed class HpSoftpaqRefProvider : IHpSoftpaqRefProvider
{
    public const string HttpClientName = "HpSoftpaqRef";
    internal static readonly TimeSpan CacheMaxAge = TimeSpan.FromHours(24);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IPowerShellInvoker _powerShell;
    private readonly ILogger<HpSoftpaqRefProvider> _logger;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public HpSoftpaqRefProvider(
        IHttpClientFactory httpClientFactory,
        IPowerShellInvoker powerShell,
        ILogger<HpSoftpaqRefProvider> logger,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(powerShell);
        ArgumentNullException.ThrowIfNull(logger);
        _httpClientFactory = httpClientFactory;
        _powerShell = powerShell;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<string?> GetReferenceXmlPathAsync(string platformId, string osToken, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(platformId);
        ArgumentException.ThrowIfNullOrWhiteSpace(osToken);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var referenceName = $"{platformId}_64_{osToken}";
            var cacheDirectory = Path.Combine(Path.GetTempPath(), "DriverUpdater", "HpSoftpaqRef", referenceName);
            var xmlPath = Path.Combine(cacheDirectory, referenceName + ".xml");

            if (File.Exists(xmlPath)
                && _clock.GetUtcNow() - File.GetLastWriteTimeUtc(xmlPath) < CacheMaxAge)
            {
                _logger.LogInformation("HP softpaq reference cache hit at {Path}", xmlPath);
                return xmlPath;
            }

            Directory.CreateDirectory(cacheDirectory);
            var cabUrl = $"https://hpia.hpcloud.hp.com/ref/{platformId.ToLowerInvariant()}/{referenceName}.cab";
            var cabPath = Path.Combine(cacheDirectory, referenceName + ".cab");

            _logger.LogInformation("Downloading HP softpaq reference from {Url}", cabUrl);
            var client = _httpClientFactory.CreateClient(HttpClientName);
            using (var response = await client.GetAsync(cabUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogInformation(
                        "HP softpaq reference not available for platform {Platform} / {Os} (HTTP {Status})",
                        platformId, osToken, (int)response.StatusCode);
                    return null;
                }
                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var output = File.Create(cabPath);
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }

            var expandPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "expand.exe");
            var script = $"& {QuotePowerShellLiteral(expandPath)} -F:* {QuotePowerShellLiteral(cabPath)} {QuotePowerShellLiteral(cacheDirectory)}; exit $LASTEXITCODE";
            var result = await _powerShell.InvokeAsync(script, cancellationToken).ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                _logger.LogWarning("HP softpaq reference expand failed (exit {Code})", result.ExitCode);
                return null;
            }

            var extractedXml = Directory.EnumerateFiles(cacheDirectory, "*.xml", SearchOption.TopDirectoryOnly)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (extractedXml is null)
            {
                _logger.LogWarning("HP softpaq reference cab contained no XML");
                return null;
            }

            if (!extractedXml.Equals(xmlPath, StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(extractedXml, xmlPath, overwrite: true);
            }
            File.SetLastWriteTimeUtc(xmlPath, _clock.GetUtcNow().UtcDateTime);
            TryDelete(cabPath);
            return xmlPath;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "HP softpaq reference download failed");
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
        }
    }

    private static string QuotePowerShellLiteral(string value) =>
        $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
}
