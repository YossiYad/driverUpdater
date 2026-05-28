using System.Management;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using DriverUpdater.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace DriverUpdater.Infrastructure.Wmi;

[SupportedOSPlatform("windows")]
public sealed class WmiQueryRunner : IWmiQueryRunner
{
    private readonly ILogger<WmiQueryRunner> _logger;

    public WmiQueryRunner(ILogger<WmiQueryRunner> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> QueryAsync(
        string scope,
        string wqlQuery,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(wqlQuery);

        _logger.LogDebug("WMI query: {Scope} | {Query}", scope, wqlQuery);

        var channel = System.Threading.Channels.Channel.CreateUnbounded<IReadOnlyDictionary<string, object?>>(
            new System.Threading.Channels.UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true,
                AllowSynchronousContinuations = false
            });

        var producer = Task.Run(() =>
        {
            try
            {
                var managementScope = new ManagementScope(scope);
                managementScope.Connect();

                using var searcher = new ManagementObjectSearcher(
                    managementScope,
                    new ObjectQuery(wqlQuery),
                    new System.Management.EnumerationOptions { ReturnImmediately = true, Rewindable = false });

                using var collection = searcher.Get();
                foreach (var item in collection)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var managementObject = (ManagementObject)item;
                    var snapshot = ReadProperties(managementObject);
                    channel.Writer.TryWrite(snapshot);
                }

                channel.Writer.Complete();
            }
            catch (OperationCanceledException)
            {
                channel.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "WMI query failed: {Scope} | {Query}", scope, wqlQuery);
                channel.Writer.TryComplete(ex);
            }
        }, cancellationToken);

        await foreach (var row in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return row;
        }

        await producer.ConfigureAwait(false);
    }

    private static IReadOnlyDictionary<string, object?> ReadProperties(ManagementObject managementObject)
    {
        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in managementObject.Properties)
        {
            dict[property.Name] = property.Value;
        }
        return dict;
    }
}
