# THE PC'S H.265 DECODER — built from source, bundled with the app.
#
# **Why the app carries a decoder at all.** The camera speaks H.265 and nothing else (we measured it:
# every quality it offers, at every resolution, is H.265), and Windows does not ship an H.265 decoder.
# Media Foundation only gets one from the *HEVC Video Extensions*, a separate Store download. That made
# the picture depend on a parent finding a Store page at 3am — and on a machine that has no Store at
# all, or no account signed into it, the picture was simply never coming. A baby monitor whose video
# depends on an errand is not a baby monitor that works out of the box, so the decoder comes with us.
#
# **Why libde265 and not FFmpeg.** FFmpeg's own NuGet runtime is 139 MB (avcodec alone is 84 MB) and
# has no arm64 build; it would roughly triple what every PC downloads through the updater, to use one
# decoder out of hundreds. libde265 does H.265 and nothing else: **772 KB**.
#
# **Why the CRT is static.** Linked the normal way, the DLL needs MSVCP140/VCRUNTIME140 — the VC++
# redistributable, which a fresh Windows may not have. Then the decoder fails to load on exactly the
# machines this whole exercise exists to serve. Statically linked, it depends on KERNEL32 and nothing
# else. That is checked below, not hoped for: a dependency creeping back in is a picture that vanishes
# on someone else's PC and never on ours.
#
# The output is cached — a normal build never rebuilds it. CI builds it once per release.
#
#   .\windows\hevc\build-libde265.ps1              # x64
#   .\windows\hevc\build-libde265.ps1 -Platform arm64
#   .\windows\hevc\build-libde265.ps1 -Force       # rebuild even if cached
[CmdletBinding()]
param(
    [ValidateSet('x64', 'arm64')]
    [string]$Platform = 'x64',

    [switch]$Force
)

$ErrorActionPreference = 'Stop'

# Run a native command and judge it by its EXIT CODE, not by whether it wrote to stderr.
# Windows PowerShell wraps a native command's stderr in error records, which 'Stop' then makes fatal —
# so git saying "Cloning into..." (which is git being happy) would otherwise abort the build.
function Invoke-Native([scriptblock]$Command, [string]$FailureMessage) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $Command 2>&1 | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray }
        $code = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previous
    }

    if ($code -ne 0) { throw $FailureMessage }
}

# Pinned, deliberately. A decoder that changes under us between releases is a picture that breaks for
# reasons no one can reproduce — and this is the one dependency the app cannot fall back from.
$Version = 'v1.0.15'
$Repo = 'https://github.com/strukturag/libde265.git'

$here = $PSScriptRoot
$src = Join-Path $here 'build/src'
$bld = Join-Path $here "build/$Platform"
$out = Join-Path $here "bin/$Platform"
$dll = Join-Path $out 'libde265.dll'

# What the cached .dll would have to have been built from to be worth keeping. The pinned tag is not
# enough on its own: this decoder is patched (see below), and a .dll built from the same tag *before*
# that patch is a decoder whose picture freezes within a minute of every session, with nothing in the
# log to say so. "The file exists" is the kind of cache check that ships that .dll for months, so the
# stamp names the patch too — change the patch, and every cached build rebuilds itself.
$builtFrom = "$Version+desk27-suppressed-picture-releases-its-slot"
$stamp = "$dll.built-from"

if ((Test-Path $dll) -and -not $Force -and
    (Test-Path $stamp) -and ((Get-Content $stamp -Raw).Trim() -eq $builtFrom)) {
    Write-Host "==> libde265 already built: $dll" -ForegroundColor Green
    return
}

# --- the toolchain ---------------------------------------------------------
# vswhere with -all: a VS instance can sit in a state the default query hides (this is the same trap
# documented for MSBuild in CLAUDE.md — and it bit here too: cl.exe existed on disk while vswhere
# reported no instance at all, because the C++ workload was never installed).
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$vsPath = & $vswhere -all -prerelease -latest -version '[17.0,)' -products * `
    -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath | Select-Object -First 1
if (-not $vsPath) {
    # Fall back to any instance: -requires can under-report on a Community install.
    $vsPath = & $vswhere -all -prerelease -latest -version '[17.0,)' -products * -property installationPath |
        Select-Object -First 1
}

$vcvars = Join-Path $vsPath 'VC/Auxiliary/Build/vcvarsall.bat'
if (-not (Test-Path $vcvars)) {
    throw "Visual Studio's C++ tools are not installed (no vcvarsall.bat). Install the 'Desktop development with C++' workload."
}

$cmakeCommand = Get-Command cmake -ErrorAction SilentlyContinue
$cmake = if ($cmakeCommand) { $cmakeCommand.Source } else { $null }
foreach ($candidate in @("${env:ProgramFiles}\CMake\bin\cmake.exe", (Join-Path $vsPath 'Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe'))) {
    if (-not $cmake -and (Test-Path $candidate)) { $cmake = $candidate }
}
if (-not $cmake) { throw 'CMake was not found. Install it (winget install Kitware.CMake) or add VS''s C++ CMake tools.' }

# x64 builds native; arm64 is cross-compiled from an x64 host, which is what CI runs on.
$arch = if ($Platform -eq 'arm64') { 'x64_arm64' } else { 'x64' }

# --- the source ------------------------------------------------------------
if (-not (Test-Path $src)) {
    Write-Host "==> Fetching libde265 $Version" -ForegroundColor Cyan
    New-Item -ItemType Directory -Force (Split-Path $src) | Out-Null
    # git talks on stderr even when it is happy ("Cloning into..."), and Windows PowerShell turns a
    # native command's stderr into error records that $ErrorActionPreference='Stop' makes fatal. The
    # exit code is the only honest signal here.
    Invoke-Native { git clone --depth 1 --branch $Version $Repo $src } 'could not fetch libde265'
}

# --- the patch -------------------------------------------------------------
# DESK-27: libde265 leaks a picture-buffer slot for every faulty picture it suppresses — and DESK-26
# is why suppression is on, so the leak is ours by construction rather than bad luck.
#
# push_picture_to_output_queue() drops a faulty picture on the floor instead of queueing it for
# output, but leaves PicOutputFlag set. The decoder's own invariant (image.h) is that a slot is free
# only when `PicOutputFlag == false && PicState == UnusedForReference`, so a suppressed picture holds
# its slot for ever. Lose enough reference frames — a dropped backlog (LIVE-8) will do it — and the
# buffer fills with pictures nobody can extract: de265_decode answers IMAGE_BUFFER_FULL ("extract
# some images before continuing") while de265_get_next_picture hands back nothing. Full, so it will
# not decode; silent, so it cannot be drained. It never recovers on its own.
#
# Measured on a real C100 before this existed: the picture froze ~25 s into every session and stayed
# frozen — while frames arrived at 20/s, audio played, the status said Live and the log said nothing.
# A photograph of a sleeping baby, held up for as long as anyone cared to look at it.
#
# Clearing the flag is exactly what the surrounding code does elsewhere (dpb.cc) to let a slot go: the
# picture is still never shown, which is all DESK-26 asks — it just stops being immortal.
$decctx = Join-Path $src 'libde265/decctx.cc'
$marker = 'outimg->PicOutputFlag = false; // baby-monitor DESK-27'
# Normalised to LF before anything is matched: git hands this file over with whatever line endings
# core.autocrlf happens to want on the machine doing the clone, so a patch that matches CRLF would
# apply here and silently miss on a runner configured the other way — shipping an unpatched decoder
# from a build that looked perfectly green. MSVC does not care which it compiles.
$text = [IO.File]::ReadAllText($decctx).Replace("`r`n", "`n")
if (-not $text.Contains($marker)) {
    $needle = "    if (outimg->integrity != INTEGRITY_CORRECT &&`n        param_suppress_faulty_pictures) {`n    }`n"
    if (-not $text.Contains($needle)) {
        throw "libde265 $Version no longer has the faulty-picture suppression block this patch fixes. " +
              'Check whether upstream fixed the leak (then drop this patch) or moved it (then update it) — ' +
              'do NOT build unpatched: the picture freezes within a minute and nothing says so.'
    }

    $fixed = "    if (outimg->integrity != INTEGRITY_CORRECT &&`n" +
             "        param_suppress_faulty_pictures) {`n" +
             "      // A suppressed picture is never handed to the caller, so nothing will ever release it.`n" +
             "      // Say it is not for output and the DPB can reclaim the slot (image.h: can_be_released).`n" +
             "      $marker`n" +
             "    }`n"
    [IO.File]::WriteAllText($decctx, $text.Replace($needle, $fixed))
    Write-Host '==> Patched libde265: a suppressed picture gives its buffer slot back (DESK-27)' -ForegroundColor Cyan
}

# --- the build -------------------------------------------------------------
Write-Host "==> Building libde265 $Version ($Platform)" -ForegroundColor Cyan
Remove-Item $bld -Recurse -Force -ErrorAction SilentlyContinue

# CMAKE_POLICY_VERSION_MINIMUM: libde265 asks for cmake_minimum_required(3.3.2) and CMake 4 refuses
# anything under 3.5. CMP0091 NEW is what makes CMAKE_MSVC_RUNTIME_LIBRARY mean anything at all —
# without it the static CRT is silently ignored and the redistributable dependency comes back.
$configure = @(
    '-S', "`"$src`"", '-B', "`"$bld`"", '-G', '"NMake Makefiles"',
    '-DCMAKE_POLICY_VERSION_MINIMUM=3.5',
    '-DCMAKE_POLICY_DEFAULT_CMP0091=NEW',
    '-DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded',
    '-DCMAKE_BUILD_TYPE=Release',
    '-DBUILD_SHARED_LIBS=ON',
    '-DENABLE_SDL=OFF'
) -join ' '

# One shell: vcvarsall sets the compiler up for this process only, so the build has to run inside it.
$script = @"
@echo off
call "$vcvars" $arch >nul 2>&1 || exit /b 1
"$cmake" $configure || exit /b 1
"$cmake" --build "$bld" || exit /b 1
"@
$cmd = Join-Path $env:TEMP "bm-de265-$Platform.cmd"
Set-Content -Path $cmd -Value $script -Encoding ASCII
Invoke-Native { cmd /c $cmd } "libde265 did not build ($Platform)"
Remove-Item $cmd -Force -ErrorAction SilentlyContinue

$built = Get-ChildItem $bld -Filter 'libde265.dll' -Recurse | Select-Object -First 1
if (-not $built) { throw 'libde265 built but produced no DLL' }

New-Item -ItemType Directory -Force $out | Out-Null
Copy-Item $built.FullName $dll -Force

# --- the promise, checked --------------------------------------------------
# The whole point of the static CRT is that this DLL loads on a machine with nothing installed. If a
# redistributable dependency ever creeps back in, fail here — loudly, on our machine — rather than on
# a parent's PC where it shows up as a picture that never appears.
$dumpbin = Get-ChildItem (Join-Path $vsPath 'VC/Tools/MSVC') -Filter 'dumpbin.exe' -Recurse -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match 'Hostx64\\x64' } | Select-Object -First 1
if ($dumpbin) {
    $deps = & $dumpbin.FullName /dependents $dll 2>&1 | Select-String -Pattern '^\s+\S+\.dll' | ForEach-Object { $_.Line.Trim() }
    $bad = $deps | Where-Object { $_ -match 'VCRUNTIME|MSVCP|api-ms-win-crt' }
    if ($bad) {
        throw "libde265.dll needs the VC++ redistributable ($($bad -join ', ')) - it must be statically linked, or it will not load on a clean Windows."
    }
    Write-Host "    depends only on: $($deps -join ', ')" -ForegroundColor DarkGray
}

# Written last, and only here: the stamp says "this exact .dll came from that exact source". Written
# any earlier and a build that failed halfway would leave a stamp vouching for a decoder nobody built.
Set-Content -Path $stamp -Value $builtFrom -Encoding ASCII

Write-Host "==> $dll ($([math]::Round((Get-Item $dll).Length / 1KB)) KB)" -ForegroundColor Green
