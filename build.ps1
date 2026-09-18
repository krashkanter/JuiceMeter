<#
.SYNOPSIS
    Builds Juice Meter into dist\.

.PARAMETER SelfContained
    Bundle the .NET runtime so the exe runs on a machine with nothing installed.
    Makes a much bigger file.

.PARAMETER Clean
    Wipe bin, obj and dist first.

.EXAMPLE
    .\build.ps1
    .\build.ps1 -SelfContained
#>
[CmdletBinding()]
param(
    [switch]$SelfContained,
    [switch]$Clean
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'src\JuiceMeter\JuiceMeter.csproj'
$dist = Join-Path $root 'dist'

# A dotnet.exe on PATH is not necessarily an SDK. A runtime-only install ships
# the same executable and answers 'No .NET SDKs were found' to every build, so
# each candidate has to be asked what it actually has before being trusted.
function Test-DotnetSdk {
    param([string]$Path)

    if (-not (Test-Path $Path)) { return $false }

    $sdks = & $Path --list-sdks 2>$null
    return ($LASTEXITCODE -eq 0) -and ($sdks -ne $null) -and ($sdks.Count -gt 0)
}

function Find-Dotnet {
    $candidates = @()

    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($command) { $candidates += $command.Source }

    $candidates += Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
    $candidates += Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'

    foreach ($candidate in $candidates) {
        if (Test-DotnetSdk $candidate) { return $candidate }
    }

    throw 'The .NET 8 SDK is required (a runtime-only install is not enough). Get it from https://dot.net'
}

$dotnet = Find-Dotnet

# Keep the host and the SDK on the same install, or a per-user SDK will resolve
# framework references against whatever is in Program Files.
$env:DOTNET_ROOT = Split-Path $dotnet -Parent
Write-Host "Using $dotnet" -ForegroundColor DarkGray

if ($Clean) {
    Write-Host 'Cleaning' -ForegroundColor Cyan
    foreach ($path in @($dist, (Join-Path $root 'src\JuiceMeter\bin'), (Join-Path $root 'src\JuiceMeter\obj'))) {
        if (Test-Path $path) { Remove-Item $path -Recurse -Force }
    }
}

$arguments = @(
    'publish', $project,
    '-c', 'Release',
    '-r', 'win-x64',
    '-o', $dist,
    "-p:SelfContained=$($SelfContained.IsPresent.ToString().ToLower())",
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:DebugType=embedded',
    '--nologo'
)

Write-Host ("Publishing ({0})" -f $(if ($SelfContained) { 'self-contained' } else { 'framework-dependent' })) -ForegroundColor Cyan
& $dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw "Publish failed with exit code $LASTEXITCODE" }

$exe = Join-Path $dist 'JuiceMeter.exe'
if (-not (Test-Path $exe)) { throw "Expected $exe but it is not there" }

$sizeMb = (Get-Item $exe).Length / 1MB

Write-Host ''
Write-Host ("Built {0} ({1:N1} MB)" -f $exe, $sizeMb) -ForegroundColor Green
Write-Host ''
Write-Host 'Try it without the GUI:' -ForegroundColor DarkGray
Write-Host ("  {0} --probe 6" -f $exe) -ForegroundColor DarkGray
Write-Host ''
Write-Host 'Run it as administrator for CPU package power and self-calibration.' -ForegroundColor DarkGray
