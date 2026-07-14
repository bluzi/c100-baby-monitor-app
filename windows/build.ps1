# Builds the Windows app into a folder you can copy, zip and run.
#
#   .\windows\build.ps1                 # a debug build, for looking at
#   .\windows\build.ps1 -Configuration Release -Version 0.1.42
#
# The output is SELF-CONTAINED and UNPACKAGED: everything the app needs, including the Windows App
# SDK itself, sits in the folder. That is what lets the updater (UPD-5) swap a version by replacing
# files — no installer, no MSIX identity, and no dialog for a parent to answer at 3am.
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [string]$Version = '0.0.0',

    [ValidateSet('x64', 'arm64')]
    [string]$Platform = 'x64',

    [switch]$Run
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$app = Join-Path $PSScriptRoot 'src/BabyMonitor.App/BabyMonitor.App.csproj'
$out = Join-Path $PSScriptRoot "build/$Platform"

Write-Host '==> The spec suite (the monitor itself)' -ForegroundColor Cyan
dotnet test (Join-Path $PSScriptRoot 'tests/BabyMonitor.Core.Tests/BabyMonitor.Core.Tests.csproj') `
    --configuration $Configuration --nologo
if ($LASTEXITCODE -ne 0) { throw 'the spec suite failed — nothing is built from a red suite' }

Write-Host "==> Publishing $Configuration $Version ($Platform)" -ForegroundColor Cyan
dotnet publish $app `
    --configuration $Configuration `
    --runtime "win-$Platform" `
    --output $out `
    -p:Platform=$Platform `
    -p:Version=$Version `
    -p:InformationalVersion=$Version `
    --nologo
if ($LASTEXITCODE -ne 0) { throw 'the app did not build' }

Write-Host "==> $out" -ForegroundColor Green

if ($Run) {
    & (Join-Path $out 'BabyMonitor.exe')
}
