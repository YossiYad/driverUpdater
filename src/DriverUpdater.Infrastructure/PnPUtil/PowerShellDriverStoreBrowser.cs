using System.Globalization;
using System.Text.Json;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using Microsoft.Extensions.Logging;

namespace DriverUpdater.Infrastructure.PnPUtil;

/// <summary>
/// Enumerates the third-party DriverStore packages with the DISM PowerShell cmdlet
/// <c>Get-WindowsDriver -Online</c> serialized to JSON. pnputil's <c>/enum-drivers</c> output is
/// localized per Windows display language, so parsing its labels would break on non-English
/// machines; the cmdlet returns structured objects that serialize the same everywhere.
/// </summary>
public sealed class PowerShellDriverStoreBrowser : IDriverStoreBrowser
{
    private const string Script =
        "Get-WindowsDriver -Online | Select-Object Driver, OriginalFileName, ProviderName, ClassName, Version, Date | ConvertTo-Json -Compress";

    private readonly IPowerShellInvoker _powerShell;
    private readonly ILogger<PowerShellDriverStoreBrowser> _logger;

    public PowerShellDriverStoreBrowser(
        IPowerShellInvoker powerShell,
        ILogger<PowerShellDriverStoreBrowser> logger)
    {
        ArgumentNullException.ThrowIfNull(powerShell);
        ArgumentNullException.ThrowIfNull(logger);
        _powerShell = powerShell;
        _logger = logger;
    }

    public async Task<IReadOnlyList<DriverStorePackage>> EnumeratePackagesAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await _powerShell.InvokeAsync(Script, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            _logger.LogWarning(
                "Get-WindowsDriver failed with exit {Code}: {Error}",
                result.ExitCode, result.StandardError.Trim());
            return Array.Empty<DriverStorePackage>();
        }

        return ParsePackages(result.StandardOutput, _logger);
    }

    internal static IReadOnlyList<DriverStorePackage> ParsePackages(string json, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<DriverStorePackage>();
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            // A single driver serializes as one object, multiple as an array.
            var elements = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray().ToArray()
                : new[] { doc.RootElement };

            var packages = new List<DriverStorePackage>();
            foreach (var element in elements)
            {
                var published = ReadString(element, "Driver");
                if (string.IsNullOrWhiteSpace(published))
                {
                    continue;
                }
                packages.Add(new DriverStorePackage(
                    PublishedName: published,
                    OriginalFileName: FileNameOnly(ReadString(element, "OriginalFileName")),
                    Provider: ReadString(element, "ProviderName"),
                    ClassName: ReadString(element, "ClassName"),
                    Version: ReadString(element, "Version"),
                    Date: ParseDate(ReadString(element, "Date"))));
            }
            return packages;
        }
        catch (JsonException ex)
        {
            logger?.LogWarning(ex, "Could not parse Get-WindowsDriver JSON output");
            return Array.Empty<DriverStorePackage>();
        }
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? FileNameOnly(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : Path.GetFileName(path);

    private static DateOnly? ParseDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        // ConvertTo-Json emits DISM dates either as "\/Date(1717372800000)\/" or ISO-ish text.
        var msStart = raw.IndexOf("Date(", StringComparison.Ordinal);
        if (msStart >= 0)
        {
            var msEnd = raw.IndexOf(')', msStart);
            if (msEnd > msStart
                && long.TryParse(raw[(msStart + 5)..msEnd], NumberStyles.Integer, CultureInfo.InvariantCulture, out var epochMs))
            {
                return DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(epochMs).UtcDateTime);
            }
        }

        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? DateOnly.FromDateTime(parsed.UtcDateTime)
            : null;
    }
}
