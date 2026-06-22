#requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [switch]$IncludeIntegration,

    [string]$ResultsDirectory = (Join-Path $PSScriptRoot '..\artifacts\test-results'),

    [string]$BaseOutputPath = (Join-Path $PSScriptRoot '..\artifacts\test-bin')
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$results = [System.IO.Path]::GetFullPath($ResultsDirectory)
$output = [System.IO.Path]::GetFullPath($BaseOutputPath)

New-Item -ItemType Directory -Path $results -Force | Out-Null
New-Item -ItemType Directory -Path $output -Force | Out-Null

$arguments = @(
    'test',
    (Join-Path $repoRoot 'DriverUpdater.slnx'),
    '--configuration', $Configuration,
    '--logger', 'trx;LogFilePrefix=DriverUpdater',
    '--results-directory', $results,
    '--blame-hang-timeout', '3m',
    "-p:BaseOutputPath=$($output.TrimEnd('\'))\"
)

if (-not $IncludeIntegration)
{
    $arguments += @('--filter', 'Category!=Integration')
}

Write-Host "Running tests. Results: $results"
& dotnet @arguments
$exitCode = $LASTEXITCODE

$trxFiles = Get-ChildItem -LiteralPath $results -Filter '*.trx' -File -ErrorAction SilentlyContinue
Write-Host "Test result files:"
$trxFiles | Select-Object FullName, Length, LastWriteTime | Format-Table -AutoSize

if ($exitCode -ne 0)
{
    Write-Host "Tests failed. Inspect the TRX files and any Sequence_*.xml blame file in $results." -ForegroundColor Red
    exit $exitCode
}

Write-Host "Tests passed. Diagnostic results were retained in $results." -ForegroundColor Green
