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

    # The first-install setup .exe (needs Inno Setup: `choco install innosetup`). The app itself never
    # runs it again — it updates itself from the .zip (UPD-3/5).
    [switch]$Installer,

    [switch]$Run
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$app = Join-Path $PSScriptRoot 'src/BabyMonitor.App/BabyMonitor.App.csproj'
$out = Join-Path $PSScriptRoot "build/$Platform"

# Built with MSBuild from Visual Studio, NOT `dotnet publish` — the same reason CI is (see release.yml).
# The Windows App SDK generates the app's resources.pri with an MSBuild task
# (Microsoft.Build.Packaging.Pri.Tasks) that ships with VS's MSBuild, not with the .NET SDK's; under
# `dotnet` it is looked for in the SDK tree, is not there, and the build dies with MSB4062. So find VS's
# MSBuild. `-all` because a VS instance can sit in a state the default vswhere query filters out (e.g.
# right after a workload is added); `[17.0,)` so an old VS 2019 that cannot build .NET 8 is never picked.
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$msbuild = & $vswhere -all -prerelease -latest -version '[17.0,)' -products * `
    -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild -or -not (Test-Path $msbuild)) {
    throw "MSBuild from Visual Studio 2022+ was not found. Install VS's '.NET desktop development' workload."
}

Write-Host '==> The spec suite (the monitor itself)' -ForegroundColor Cyan
dotnet test (Join-Path $PSScriptRoot 'tests/BabyMonitor.Core.Tests/BabyMonitor.Core.Tests.csproj') `
    --configuration $Configuration --nologo
if ($LASTEXITCODE -ne 0) { throw 'the spec suite failed — nothing is built from a red suite' }

Write-Host "==> Publishing $Configuration $Version ($Platform)" -ForegroundColor Cyan
# Force the app's resource index to be rebuilt, every time.
#
# The Windows App SDK generates BabyMonitor.pri with an MSBuild task that decides it is up to date when
# the *set* of resource files has not changed — and editing a .xaml does not change the set. So an
# incremental build happily recompiles MainWindow.xaml into a new .xbf and leaves yesterday's index
# sitting beside it. The pair no longer agree, `ms-appx:///MainWindow.xaml` cannot be resolved, and
# InitializeComponent throws XamlParseException — which App.OnUnhandledException catches (DESK-6),
# leaving a live process with NO WINDOW. The monitor that never appears, from nothing but a stale file.
# Deleting the index costs a second and makes that state unreachable. (This is the incremental-build
# cousin of the publish bug the csproj's _IncludeAppXamlResourcesInPublish target fixes; CI never sees
# either, because CI always builds clean — which is exactly why it has to be caught here.)
Get-ChildItem (Join-Path $PSScriptRoot 'src/BabyMonitor.App') -Include 'BabyMonitor.pri' -Recurse -ErrorAction SilentlyContinue |
    Remove-Item -Force -ErrorAction SilentlyContinue
# /restore in the same call restores with the RID (a self-contained publish needs a RID-specific
# restore). PublishProtocol=FileSystem is load-bearing: without it /t:Publish builds but never runs the
# filesystem-publish that copies the self-contained app to PublishDir. The trailing forward slash on
# PublishDir dodges the Windows backslash-in-quoted-arg trap. (The app's own PRI and its compiled XAML
# are added back into this publish by a target in BabyMonitor.App.csproj — without it the published app
# has no ms-appx:///…xaml and never shows a window.)
& $msbuild $app `
    /restore /t:Publish `
    /p:Configuration=$Configuration `
    /p:Platform=$Platform `
    /p:RuntimeIdentifier="win-$Platform" `
    /p:SelfContained=true `
    /p:PublishProtocol=FileSystem `
    /p:PublishDir="$out/" `
    /p:Version=$Version `
    /p:InformationalVersion=$Version `
    /nologo
if ($LASTEXITCODE -ne 0) { throw 'the app did not build' }

Write-Host "==> $out" -ForegroundColor Green

if ($Installer) {
    $iscc = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe'
    if (-not (Test-Path $iscc)) { throw "Inno Setup is not installed: choco install innosetup" }

    Write-Host '==> The first-install setup' -ForegroundColor Cyan
    & $iscc "/DAppVersion=$Version" "/Fbabymonitor-v$Version-windows-setup" `
        "/O$PSScriptRoot" (Join-Path $PSScriptRoot 'installer/BabyMonitor.iss')
    if ($LASTEXITCODE -ne 0) { throw 'the installer did not build' }

    Write-Host "==> $(Join-Path $PSScriptRoot "babymonitor-v$Version-windows-setup.exe")" -ForegroundColor Green
}

if ($Run) {
    & (Join-Path $out 'BabyMonitor.exe')
}
