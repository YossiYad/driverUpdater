#requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$Version
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

$configuration = 'Release'
$runtime = 'win-x64'
$appProject = Join-Path $repoRoot 'src\DriverUpdater.App\DriverUpdater.App.csproj'
$setupProject = Join-Path $repoRoot 'src\DriverUpdater.Setup\DriverUpdater.Setup.csproj'
$publishDirectory = Join-Path $repoRoot "src\DriverUpdater.App\bin\$configuration\net10.0-windows\$runtime\publish"
$outputDirectory = Join-Path $repoRoot 'build\output'

Write-Host ''
Write-Host '=== DriverUpdater release ==='
Write-Host "Version: $Version"
Write-Host "Configuration: $configuration"
Write-Host "Runtime: $runtime"
Write-Host ''

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

Write-Host 'Restoring solution...'
Invoke-Checked dotnet restore (Join-Path $repoRoot 'DriverUpdater.slnx')
Invoke-Checked dotnet restore $appProject --runtime $runtime

Write-Host 'Running tests...'
Invoke-Checked dotnet test (Join-Path $repoRoot 'DriverUpdater.slnx') --no-restore --configuration $configuration --filter 'Category!=Integration'

Write-Host 'Running text lint...'
& (Join-Path $PSScriptRoot 'lint-text.ps1') -RepoRoot $repoRoot
if ($LASTEXITCODE -ne 0) {
    throw "Text lint exited with code $LASTEXITCODE."
}

# dotnet publish does not remove files that disappeared between versions. A stale DLL in
# this directory would be silently packed into the next installer, so always recreate it.
$publishFullPath = [System.IO.Path]::GetFullPath($publishDirectory)
if (-not $publishFullPath.StartsWith($repoRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to clean publish directory outside the repository: $publishFullPath"
}
if (Test-Path -LiteralPath $publishFullPath) {
    Remove-Item -LiteralPath $publishFullPath -Recurse -Force
}

Write-Host 'Publishing app...'
Invoke-Checked dotnet publish $appProject --no-restore --configuration $configuration --runtime $runtime --self-contained true '-p:PublishSingleFile=false' '-p:IncludeNativeLibrariesForSelfExtract=true'

$publishedAssembly = Join-Path $publishDirectory 'DriverUpdater.dll'
$assemblyVersion = [System.Reflection.AssemblyName]::GetAssemblyName($publishedAssembly).Version
$numericVersion = ($Version -split '[-+]')[0]
$expectedAssemblyVersion = [Version]"$numericVersion.0"
if ($assemblyVersion -ne $expectedAssemblyVersion) {
    throw "Published assembly version $assemblyVersion does not match release version $Version."
}

Write-Host 'Ensuring vpk tool...'
Invoke-Checked dotnet tool restore

New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null

# Preserve older full packages so Velopack can generate a delta, but remove every output
# that could be mistaken for this release if packaging stops halfway through.
$currentOutputs = @(
    "DriverUpdater-$Version-full.nupkg",
    "DriverUpdater-$Version-delta.nupkg",
    'DriverUpdater-win-Setup.exe',
    'DriverUpdater-win-Portable.zip',
    'RELEASES',
    'releases.win.json',
    'assets.win.json'
)
foreach ($name in $currentOutputs) {
    $path = Join-Path $outputDirectory $name
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Force
    }
}

Write-Host 'Packaging with Velopack...'
Invoke-Checked dotnet vpk pack `
    --packId DriverUpdater `
    --packTitle DriverUpdater `
    --packAuthors 'Yossi Yadgar' `
    --packVersion $Version `
    --packDir $publishDirectory `
    --mainExe DriverUpdater.exe `
    --icon (Join-Path $repoRoot 'src\DriverUpdater.App\Assets\app.ico') `
    --outputDir $outputDirectory `
    --runtime $runtime

$velopackSetupPath = Join-Path $outputDirectory 'DriverUpdater-win-Setup.exe'
$setupStagingDirectory = Join-Path $outputDirectory '.setup-staging'
$setupStagingFullPath = [System.IO.Path]::GetFullPath($setupStagingDirectory)
if (-not $setupStagingFullPath.StartsWith($outputDirectory + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to clean setup staging directory outside the release output: $setupStagingFullPath"
}
if (Test-Path -LiteralPath $setupStagingFullPath) {
    Remove-Item -LiteralPath $setupStagingFullPath -Recurse -Force
}

New-Item -ItemType Directory -Path $setupStagingFullPath -Force | Out-Null
$velopackSetupPayload = Join-Path $setupStagingFullPath 'DriverUpdater-Velopack-Setup.exe'
$setupPublishDirectory = Join-Path $setupStagingFullPath 'publish'
Move-Item -LiteralPath $velopackSetupPath -Destination $velopackSetupPayload

Write-Host 'Building elevated repair Setup...'
$visualStudioInstallerDirectory = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
if (Test-Path -LiteralPath (Join-Path $visualStudioInstallerDirectory 'vswhere.exe')) {
    $pathEntries = $env:PATH -split [System.IO.Path]::PathSeparator
    if ($pathEntries -notcontains $visualStudioInstallerDirectory) {
        $env:PATH = $visualStudioInstallerDirectory + [System.IO.Path]::PathSeparator + $env:PATH
    }
}
Invoke-Checked dotnet publish $setupProject `
    --no-restore `
    --configuration $configuration `
    --runtime $runtime `
    --output $setupPublishDirectory `
    "-p:EmbeddedSetupPath=$velopackSetupPayload"

$repairSetupPath = Join-Path $setupPublishDirectory 'DriverUpdater-win-Setup.exe'
if (-not (Test-Path -LiteralPath $repairSetupPath)) {
    throw 'The elevated repair Setup was not produced.'
}
if ((Get-Item -LiteralPath $repairSetupPath).Length -le (Get-Item -LiteralPath $velopackSetupPayload).Length) {
    throw 'The elevated repair Setup does not appear to contain the Velopack payload.'
}

Move-Item -LiteralPath $repairSetupPath -Destination $velopackSetupPath

# Run the wrapper without elevation only for this non-installing smoke test. This verifies
# that the native launcher can extract and execute the untouched Velopack payload.
$previousCompatLayer = $env:__COMPAT_LAYER
try {
    $env:__COMPAT_LAYER = 'RunAsInvoker'
    $setupSmokeTest = Start-Process `
        -FilePath $velopackSetupPath `
        -ArgumentList '--help' `
        -WindowStyle Hidden `
        -Wait `
        -PassThru
    if ($setupSmokeTest.ExitCode -ne 0) {
        throw "The elevated repair Setup smoke test failed with exit code $($setupSmokeTest.ExitCode)."
    }
}
finally {
    if ($null -eq $previousCompatLayer) {
        Remove-Item Env:__COMPAT_LAYER -ErrorAction SilentlyContinue
    }
    else {
        $env:__COMPAT_LAYER = $previousCompatLayer
    }
}

Remove-Item -LiteralPath $setupStagingFullPath -Recurse -Force

$requiredOutputs = @(
    "DriverUpdater-$Version-full.nupkg",
    'DriverUpdater-win-Setup.exe',
    'DriverUpdater-win-Portable.zip',
    'RELEASES',
    'releases.win.json'
)
$missingOutputs = $requiredOutputs | Where-Object { -not (Test-Path -LiteralPath (Join-Path $outputDirectory $_)) }
if ($missingOutputs) {
    throw "Release packaging completed without required outputs: $($missingOutputs -join ', ')"
}

Write-Host ''
Write-Host "Release artifacts verified in $outputDirectory" -ForegroundColor Green
