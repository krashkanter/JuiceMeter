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

function Find-Dotnet {
    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }

    $local = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
    if (Test-Path $local) { return $local }

    throw 'The .NET 8 SDK is required. Get it from https://dot.net'
}

$dotnet = Find-Dotnet
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
