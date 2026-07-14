# The Windows app

A tray icon, one window that wears two shapes, and a picture. Behaviour lives in
[`spec/features/windows-shell.spec.md`](../spec/features/windows-shell.spec.md); this file is about
the code.

```
windows/
  src/BabyMonitor.Core/    THE MONITOR, in C#. Protocol, engine, DSP, alarm, watchdog. net8.0 —
                           no Windows API in it at all, so it builds and tests on any machine.
  tests/BabyMonitor.Core.Tests/
                           The spec suite, criterion for criterion, plus the interop vectors.
  src/BabyMonitor.App/     THE SHELL. WinUI 3: tray, windows, WASAPI, Media Foundation, DPAPI,
                           the updater. Everything that needs Windows, and nothing that doesn't.
```

## Why the core is a port, and what makes that safe

Windows cannot consume the Kotlin Multiplatform core the phone and the Mac share, so the protocol,
the engine and every decision they make had to be **written again** in C#. Two implementations of one
baby monitor is a real risk — they can disagree, and a disagreement here is a missed cry. The
mitigation is not care. It is evidence:

- **The wire is pinned.** `core/protocol-vectors.json` — generated from the proven c100
  implementation — is read by *both* test suites. If the C# signs a request differently, computes a
  different NaCl shared key, or discards the wrong number of RC4 keystream bytes, the build goes red.
- **The behaviour is pinned.** Every test in `core/src/commonTest` has a twin here, carrying the same
  criterion ID. "Both apps behave the same" is executed, not asserted.

If you change behaviour, change the spec, then change **both** suites. A criterion that holds on the
phone and not on the PC is a bug in one of them.

## Building

```powershell
.\windows\build.ps1                                      # debug, into windows/build/x64
.\windows\build.ps1 -Configuration Release -Version 0.1.42
.\windows\build.ps1 -Installer                           # + the first-install setup.exe
.\windows\build.ps1 -Run
```

The build is **self-contained and unpackaged**: the output folder holds everything, including the
Windows App SDK. That is what lets the updater replace a version by swapping files (UPD-5) rather
than running an installer.

## What a release ships, and why it is two things

- **`babymonitor-vX-windows-setup.exe`** — the *first install*, and nothing else. The Windows answer
  to the Mac's `.dmg`.
- **`babymonitor-vX-windows.zip`** — what the *updater* consumes, forever after. Every update but the
  first arrives this way, so it is the one that has to be right.

The setup is **per-user** (Inno Setup, `PrivilegesRequired=lowest`, into
`%LOCALAPPDATA%\Programs\BabyMonitor`), and that is load-bearing rather than a preference: the app can
rewrite that directory itself, so applying an update needs no elevation. Installed into `Program
Files` it would need an administrator — a UAC prompt at whatever hour the update lands, standing
between a parent and a running monitor. This project does not put dialogs there.

The setup deliberately has **no "start with Windows" checkbox**: the app offers that itself, once, in
words (WIN-8), and never turns it on by itself. An installer checkbox nobody read is exactly how a
monitor ends up in a startup list its owner never chose. Uninstalling leaves
`%LOCALAPPDATA%\BabyMonitor` alone — the session, the settings and the learned alarm tuning are not
something an uninstall-to-reinstall-a-fix should cost a parent.

The core and its tests need nothing but the .NET SDK, and they run anywhere:

```bash
dotnet test windows/tests/BabyMonitor.Core.Tests/BabyMonitor.Core.Tests.csproj    # macOS, Linux, Windows
```

## Reading the log

There is no `adb logcat` here, so the app writes its own — same shape as the phone's and the Mac's,
one tag per subsystem:

```
%LOCALAPPDATA%\BabyMonitor\babymonitor.log
```

## The two places a PC is weaker than a Mac, and what the app does about them

- **Sleep.** A sleeping PC runs nothing. The app holds `ES_SYSTEM_REQUIRED` while monitoring (BG-12w),
  but no application can stop the sleep a user *asks* for. So it says so before an overnight watch,
  hears `WM_POWERBROADCAST`, and on wake reports the outage **and how long it lasted** (WIN-11).
- **H.265.** Windows does not always ship an HEVC decoder, and the camera sends nothing else. Without
  it there is no picture — so the app says that in plain words, points at the free extension, and
  **keeps monitoring** (WIN-20). Sound is what monitoring means; the picture is a convenience.

## Gotchas that cost real debugging time

- **Media Foundation will not decode a byte without a frame size.** MediaCodec and VideoToolbox work
  it out from the parameter sets; Windows does not. `HevcSps.Dimensions` parses it out of the SPS —
  which is also where the window gets the camera's shape (WIN-19).
- **The camera sends VPS/SPS/PPS in their own access units**, so every keyframe handed to Media
  Foundation gets them prepended. A decoder that started late has nothing to start from otherwise.
- **DPAPI keys on the user, not the binary.** That is the whole reason it is used here: an update
  replaces every byte of the app and the stored session still opens, with no prompt (AUTH-6w). The
  Mac had to work for that; Windows gives it away.
- **A private repo's release asset is a 302 to S3**, and S3 rejects the request if our `Authorization`
  header follows it ("Only one auth mechanism allowed"). The updater strips the header across hosts.
- **Windows will not let a running program overwrite itself.** So the swap is done *by the new
  version*: the old one starts it with `--apply-update`, and exits.
