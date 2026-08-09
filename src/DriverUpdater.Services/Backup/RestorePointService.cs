using System.Text.RegularExpressions;
using DriverUpdater.Core.Abstractions;
using DriverUpdater.Core.Models;
using DriverUpdater.Core.Results;
using Microsoft.Extensions.Logging;

namespace DriverUpdater.Services.Backup;

public sealed partial class RestorePointService : IRestorePointService
{
    internal const string ProtectionEnabledByAppMarker = "SRPROTECTIONENABLEDBYAPP=1";

    private const string CheckpointScript = @"$ErrorActionPreference = 'Stop';
$srKey = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore';
$hadFrequency = $false;
$priorFrequency = 0;
try {
    $sysDrive = $env:SystemDrive + '\';
    # System Protection ships disabled on many OEM laptops, so Checkpoint-Computer fails with
    # 'the service cannot be started because it is disabled'. Turn protection back on for the
    # system drive first (the app runs elevated) so the checkpoint can actually be created.
    # The creation-frequency throttle also has to go, otherwise Windows silently skips a
    # checkpoint created within 1440 minutes of a previous one. Both of those are machine-wide
    # settings the user did not ask to change, so the prior throttle is captured here and put
    # back in the finally block below.
    $protectionWasOff = $false;
    try {
        if (-not (Test-Path $srKey)) { New-Item -Path $srKey -Force | Out-Null; }
        $existing = Get-ItemProperty -Path $srKey -ErrorAction SilentlyContinue;
        if ($null -ne $existing -and $null -ne $existing.SystemRestorePointCreationFrequency) {
            $hadFrequency = $true;
            $priorFrequency = [int]$existing.SystemRestorePointCreationFrequency;
        }
        if ($null -ne $existing -and $null -ne $existing.DisableSR -and [int]$existing.DisableSR -ne 0) {
            $protectionWasOff = $true;
        }
        Set-ItemProperty -Path $srKey -Name 'DisableSR' -Value 0 -Type DWord -Force;
        Set-ItemProperty -Path $srKey -Name 'SystemRestorePointCreationFrequency' -Value 0 -Type DWord -Force;
    } catch { }
    try { Enable-ComputerRestore -Drive $sysDrive -ErrorAction Stop; } catch { }
    Checkpoint-Computer -Description $description -RestorePointType 'MODIFY_SETTINGS' -ErrorAction Stop;
    $rp = Get-ComputerRestorePoint | Sort-Object -Property CreationTime -Descending | Select-Object -First 1;
    if ($null -eq $rp) {
        Write-Error 'No restore point found after Checkpoint-Computer';
        exit 1;
    }
    $createdUtc = [System.Management.ManagementDateTimeConverter]::ToDateTime($rp.CreationTime).ToUniversalTime();
    Write-Output ('SEQ=' + $rp.SequenceNumber + ';DESC=' + $rp.Description + ';TIME=' + $createdUtc.ToString('o'));
    # Re-disabling protection would delete the checkpoint that was just created, so it stays on.
    # Report it instead so the app can tell the user the machine setting changed.
    if ($protectionWasOff) { Write-Output 'SRPROTECTIONENABLEDBYAPP=1'; }
} catch {
    Write-Error $_.Exception.Message;
    exit 1;
} finally {
    try {
        if ($hadFrequency) {
            Set-ItemProperty -Path $srKey -Name 'SystemRestorePointCreationFrequency' -Value $priorFrequency -Type DWord -Force;
        } else {
            Remove-ItemProperty -Path $srKey -Name 'SystemRestorePointCreationFrequency' -ErrorAction SilentlyContinue;
        }
    } catch { }
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

        if (result.StandardOutput.Contains(ProtectionEnabledByAppMarker, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "System Protection was disabled on the system drive and had to be turned on to create the restore point. " +
                "It was left on, because turning it off again would delete the checkpoint that was just created.");
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
