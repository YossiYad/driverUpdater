#requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [string]$NotesFile,

    [switch]$Draft,

    [switch]$Prerelease
)

$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$propsPath = Join-Path $repoRoot 'Directory.Build.props'
[xml]$buildProps = Get-Content -LiteralPath $propsPath
$projectVersion = [string]$buildProps.Project.PropertyGroup.Version

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = $projectVersion
}

if ($Version -notmatch '^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$') {
    throw "'$Version' is not a valid semantic version."
}
if ($Version -ne $projectVersion) {
    throw "Release version $Version does not match Directory.Build.props version $projectVersion. Update the project version first."
}

if ([System.IO.Path]::IsPathRooted($NotesFile)) {
    $notesPath = [System.IO.Path]::GetFullPath($NotesFile)
}
else {
    $notesPath = [System.IO.Path]::GetFullPath((Join-Path (Get-Location) $NotesFile))
}
if (-not (Test-Path -LiteralPath $notesPath)) {
    throw "Release notes file was not found: $notesPath"
}

$outputDirectory = Join-Path $repoRoot 'build\output'
$tagName = "v$Version"

function Invoke-Checked {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments
    )

    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath exited with code $LASTEXITCODE."
    }
}

function Resolve-ReleaseAsset {
    param([Parameter(Mandatory = $true)][string]$Name)

    $path = Join-Path $outputDirectory $Name
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Required release asset was not found: $path"
    }

    return $path
}

Write-Host ''
Write-Host '=== DriverUpdater GitHub release publish ==='
Write-Host "Version: $Version"
Write-Host "Tag: $tagName"
Write-Host ''

Write-Host 'Verifying GitHub account and Git identity...'
Invoke-Checked gh auth status
$githubUser = & gh api user --jq '.login'
if ($LASTEXITCODE -ne 0) {
    throw "gh api user exited with code $LASTEXITCODE."
}
if ($githubUser -ne 'YossiYad') {
    throw "Refusing to publish as GitHub account '$githubUser'. Expected 'YossiYad'."
}

$gitUserName = & git config user.name
if ($LASTEXITCODE -ne 0) {
    throw "git config user.name exited with code $LASTEXITCODE."
}
$gitUserEmail = & git config user.email
if ($LASTEXITCODE -ne 0) {
    throw "git config user.email exited with code $LASTEXITCODE."
}
if ($gitUserName -ne 'Yossi Yadgar' -or $gitUserEmail -ne '162101311+YossiYad@users.noreply.github.com') {
    throw "Refusing to publish with Git identity '$gitUserName <$gitUserEmail>'."
}

Write-Host 'Verifying tag...'
Invoke-Checked git rev-parse "$tagName^{}"
Invoke-Checked git ls-remote --exit-code --tags origin "refs/tags/$tagName"

& gh release view $tagName --json url *> $null
if ($LASTEXITCODE -eq 0) {
    throw "GitHub release $tagName already exists."
}

$releaseAssets = @(
    (Resolve-ReleaseAsset 'DriverUpdater-win-Setup.exe'),
    (Resolve-ReleaseAsset "DriverUpdater-$Version-full.nupkg"),
    (Resolve-ReleaseAsset 'RELEASES'),
    (Resolve-ReleaseAsset 'releases.win.json')
)

$deltaAsset = Join-Path $outputDirectory "DriverUpdater-$Version-delta.nupkg"
if (Test-Path -LiteralPath $deltaAsset) {
    $releaseAssets += $deltaAsset
}

$skippedAssets = @(
    'DriverUpdater-win-Portable.zip',
    'assets.win.json'
)
foreach ($name in $skippedAssets) {
    $path = Join-Path $outputDirectory $name
    if (Test-Path -LiteralPath $path) {
        Write-Host "Skipping non-release upload asset: $name"
    }
}
Write-Host 'GitHub adds Source code archives automatically for tag releases.'

$releaseArgs = @(
    'release',
    'create',
    $tagName
) + $releaseAssets + @(
    '--title',
    "DriverUpdater $Version",
    '--notes-file',
    $notesPath,
    '--verify-tag',
    '--latest'
)

if ($Draft) {
    $releaseArgs += '--draft'
}
if ($Prerelease) {
    $releaseArgs += '--prerelease'
}

Write-Host 'Publishing GitHub release...'
Invoke-Checked gh @releaseArgs

Write-Host ''
Write-Host "GitHub release $tagName published." -ForegroundColor Green
