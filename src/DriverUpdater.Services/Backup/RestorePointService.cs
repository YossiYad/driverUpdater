using System.Text.RegularExpressions;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using DriverUpdater.Core.Results;
using Microsoft.Extensions.Logging;

namespace DriverUpdater.Services.Backup;

public sealed partial class RestorePointService : IRestorePointService
{
    private const string CheckpointScript = @"$ErrorActionPreference = 'Stop';
try {
    Checkpoint-Computer -Description $description -RestorePointType 'MODIFY_SETTINGS' -ErrorAction Stop;
    $rp = Get-ComputerRestorePoint | Sort-Object -Property CreationTime -Descending | Select-Object -First 1;
    if ($null -eq $rp) {
        Write-Error 'No restore point found after Checkpoint-Computer';
        exit 1;
    }
    $createdUtc = [System.Management.ManagementDateTimeConverter]::ToDateTime($rp.CreationTime).ToUniversalTime();
    Write-Output ('SEQ=' + $rp.SequenceNumber + ';DESC=' + $rp.Description + ';TIME=' + $createdUtc.ToString('o'));
} catch {
    Write-Error $_.Exception.Message;
    exit 1;
}";

    private const string IsEnabledScript = @"try {
    $cfg = vssadmin list shadowstorage 2>$null;
    if ($LASTEXITCODE -eq 0) { Write-Output 'ENABLED'; } else { Write-Output 'UNKNOWN'; }
} catch { Write-Output 'UNKNOWN'; }";

    private readonly IPowerShellInvoker _powerShell;
    private readonly ILogger<RestorePointService> _logger;

    public RestorePointService(IPowerShellInvoker powerShell, ILogger<RestorePointService> logger)
    {
        ArgumentNullException.ThrowIfNull(powerShell);
        ArgumentNullException.ThrowIfNull(logger);
        _powerShell = powerShell;
        _logger = logger;
    }

    public async Task<bool> IsSystemRestoreEnabledAsync(CancellationToken cancellationToken = default)
    {
        var result = await _powerShell.InvokeAsync(IsEnabledScript, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess && result.StandardOutput.Contains("ENABLED", StringComparison.Ordinal);
    }

    public async Task<Result<RestorePointInfo>> CreateRestorePointAsync(string description, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);

        var escapedDescription = description.Replace("'", "''", StringComparison.Ordinal);
        var script = $"$description = '{escapedDescription}';\n{CheckpointScript}";

        _logger.LogInformation("Creating restore point: {Description}", description);
        var result = await _powerShell.InvokeAsync(script, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            _logger.LogWarning("Checkpoint-Computer failed (exit {Code}): {Err}", result.ExitCode, result.StandardError);
            return ResultError.From("RESTORE_POINT_FAILED", $"Checkpoint-Computer failed: {result.StandardError.Trim()}");
        }

        var info = ParseRestorePointOutput(result.StandardOutput);
        if (info is null)
        {
            _logger.LogWarning("Restore point created but output could not be parsed: {Output}", result.StandardOutput);
            return ResultError.From("RESTORE_POINT_PARSE", "Could not parse restore point output: " + result.StandardOutput);
        }

        _logger.LogInformation("Created restore point {Seq}: {Desc}", info.SequenceNumber, info.Description);
        return info;
    }

    internal static RestorePointInfo? ParseRestorePointOutput(string output)
    {
        var match = OutputPattern().Match(output);
        if (!match.Success)
        {
            return null;
        }

        var seq = match.Groups["seq"].Value;
        var desc = match.Groups["desc"].Value;
        var time = match.Groups["time"].Value;
        var created = DateTimeOffset.TryParse(time, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var dto)
            ? dto.ToUniversalTime()
            : DateTimeOffset.UtcNow;
        return new RestorePointInfo(seq, desc, created);
    }

    [GeneratedRegex(@"SEQ=(?<seq>\d+);DESC=(?<desc>.*?);TIME=(?<time>\S+)")]
    private static partial Regex OutputPattern();
}
