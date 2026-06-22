#requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Directory
)

$resolved = (Resolve-Path -LiteralPath $Directory).Path
$outputPath = Join-Path $resolved 'SHA256SUMS.txt'

$lines = Get-ChildItem -LiteralPath $resolved -File |
    Where-Object Name -ne 'SHA256SUMS.txt' |
    Sort-Object Name |
    ForEach-Object {
        $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $($_.Name)"
    }

[System.IO.File]::WriteAllLines($outputPath, $lines, [System.Text.UTF8Encoding]::new($false))
Write-Host "Wrote SHA-256 checksums to $outputPath"
