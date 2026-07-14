#requires -Version 5.1
<#
.SYNOPSIS
Fails the build when forbidden characters or attribution tokens slip into the repo.

.DESCRIPTION
Scans every tracked or non-ignored text file for:
  - The em-dash character (U+2014). Project rule: use hyphen with spaces instead.
  - AI / Claude / Anthropic attribution tokens. Project rule: no AI attribution anywhere.

Exits with code 1 on any hit.
#>

[CmdletBinding()]
param(
    [string]$RepoRoot = (Resolve-Path "$PSScriptRoot\..").Path
)

$ErrorActionPreference = 'Stop'

$forbiddenPatterns = @(
    @{ Name = 'em-dash'; Pattern = [char]0x2014 },
    @{ Name = 'claude-attribution'; Pattern = 'Co-Authored-By: Claude' },
    @{ Name = 'generated-with-claude'; Pattern = 'Generated with .*Claude' },
    @{ Name = 'anthropic-mention'; Pattern = 'anthropic\.com' }
)

$textExtensions = @('.cs', '.xaml', '.xml', '.csproj', '.props', '.targets', '.md',
                    '.json', '.yml', '.yaml', '.ps1', '.cmd', '.bat', '.sh',
                    '.editorconfig', '.gitignore', '.gitattributes', '.txt')

$skipFileNames = @('lint-text.ps1')

$hits = @()

$relativeFiles = @(& git -C $RepoRoot ls-files --cached --others --exclude-standard)
if ($LASTEXITCODE -ne 0) {
    throw 'Could not enumerate repository files with git.'
}

$relativeFiles | ForEach-Object {
    $fullPath = Join-Path $RepoRoot $_
    if (Test-Path -LiteralPath $fullPath -PathType Leaf) {
        Get-Item -LiteralPath $fullPath
    }
} | Where-Object {
    if ($skipFileNames -contains $_.Name) { return $false }
    $ext = $_.Extension.ToLowerInvariant()
    if (-not $ext -and $_.Name -in @('.editorconfig', '.gitignore', '.gitattributes')) { return $true }
    return $textExtensions -contains $ext
} | ForEach-Object {
    $file = $_.FullName
    $relativePath = $file.Substring($RepoRoot.Length).TrimStart('\', '/')
    try {
        $content = [System.IO.File]::ReadAllText($file, [System.Text.Encoding]::UTF8)
    }
    catch {
        return
    }
    if ($null -eq $content) { return }

    $lineNumber = 0
    foreach ($line in ($content -split "`r?`n")) {
        $lineNumber++
        foreach ($entry in $forbiddenPatterns) {
            if ($line -match $entry.Pattern) {
                $hits += [pscustomobject]@{
                    File    = $relativePath
                    Line    = $lineNumber
                    Pattern = $entry.Name
                    Snippet = $line.Trim()
                }
            }
        }
    }
}

if ($hits.Count -gt 0) {
    Write-Host ""
    Write-Host "lint-text: forbidden tokens found" -ForegroundColor Red
    Write-Host ""
    $hits | Format-Table -AutoSize | Out-String | Write-Host
    Write-Host "Replace em-dash with hyphen surrounded by spaces. Remove any AI attribution." -ForegroundColor Yellow
    exit 1
}

Write-Host "lint-text: clean" -ForegroundColor Green
exit 0
