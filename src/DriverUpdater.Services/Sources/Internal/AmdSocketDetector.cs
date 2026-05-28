using System.Text.RegularExpressions;
using DriverUpdater.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace DriverUpdater.Services.Sources.Internal;

public sealed partial class AmdSocketDetector : IAmdSocketDetector
{
    private const string CimV2Scope = "\\\\.\\root\\CIMV2";
    private const string BaseBoardQuery = "SELECT Manufacturer, Product FROM Win32_BaseBoard";

    private static readonly (string Socket, string ChipsetSlug) Fallback = ("am5", "x870e");

    private readonly IWmiQueryRunner _wmi;
    private readonly ILogger<AmdSocketDetector> _logger;

    public AmdSocketDetector(IWmiQueryRunner wmi, ILogger<AmdSocketDetector> logger)
    {
        ArgumentNullException.ThrowIfNull(wmi);
        ArgumentNullException.ThrowIfNull(logger);
        _wmi = wmi;
        _logger = logger;
    }

    public async Task<AmdSocketInfo> DetectAsync(CancellationToken cancellationToken = default)
    {
        string? boardProduct = null;
        await foreach (var row in _wmi.QueryAsync(CimV2Scope, BaseBoardQuery, cancellationToken).ConfigureAwait(false))
        {
            boardProduct = row.TryGetValue("Product", out var value) ? value?.ToString() : null;
            break;
        }

        if (string.IsNullOrWhiteSpace(boardProduct))
        {
            _logger.LogInformation("AmdSocketDetector: could not read baseboard product from WMI; using fallback {Socket}/{Slug}", Fallback.Socket, Fallback.ChipsetSlug);
            return new AmdSocketInfo(Fallback.Socket, Fallback.ChipsetSlug, IsFallback: true);
        }

        var info = ResolveFromProduct(boardProduct);
        if (info is not null)
        {
            _logger.LogInformation("AmdSocketDetector: detected {Socket}/{Slug} from board {Product}", info.Socket, info.ChipsetSlug, boardProduct);
            return info;
        }

        _logger.LogInformation("AmdSocketDetector: no AMD chipset keyword in board {Product}; using fallback {Socket}/{Slug}", boardProduct, Fallback.Socket, Fallback.ChipsetSlug);
        return new AmdSocketInfo(Fallback.Socket, Fallback.ChipsetSlug, IsFallback: true);
    }

    internal static AmdSocketInfo? ResolveFromProduct(string product)
    {
        var upper = product.ToUpperInvariant();
        var match = ChipsetTokenPattern().Match(upper);
        if (!match.Success)
        {
            return null;
        }

        var chipset = match.Groups["chipset"].Value;
        var socket = MapChipsetToSocket(chipset);
        if (socket is null)
        {
            return null;
        }

        return new AmdSocketInfo(socket, chipset.ToLowerInvariant(), IsFallback: false);
    }

    private static string? MapChipsetToSocket(string chipset) => chipset switch
    {
        "X870E" or "X870" or "B850" or "B840" or "B650E" or "B650" or "A620" or "X670E" or "X670" => "am5",
        "X570" or "X470" or "X370" or "B550" or "B450" or "B350" or "A520" or "A320" => "am4",
        "TRX50" or "WRX90" => "str5",
        "TRX40" or "WRX80" => "strx4",
        "X399" => "tr4",
        _ => null
    };

    [GeneratedRegex(@"(?<chipset>X870E|X670E|B650E|X870|X670|B850|B840|B650|A620|X570|X470|X370|B550|B450|B350|A520|A320|TRX50|TRX40|WRX90|WRX80|X399)", RegexOptions.IgnoreCase)]
    private static partial Regex ChipsetTokenPattern();
}

public interface IAmdSocketDetector
{
    Task<AmdSocketInfo> DetectAsync(CancellationToken cancellationToken = default);
}

public sealed record AmdSocketInfo(string Socket, string ChipsetSlug, bool IsFallback);
