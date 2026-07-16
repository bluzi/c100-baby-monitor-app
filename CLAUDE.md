# C100 Baby Monitor

Turns Xiaomi cameras into a baby monitor. **Three things matter, in this order — keep them in mind
in everything you do here:**

1. **Reliability.** It should always work, and it must say so when it stops working. Silence must
   never be mistaken for a calm baby. We are in charge of babies' lives.
2. **Ease of use.** Good UX, easy to use — including at 3am, half-asleep, in the dark.
3. **Simplicity.** It is what makes the other two possible: simple code fails less, and a simple
   app is easier to use.

When these conflict, the earlier one wins. A clever feature that risks a missed cry is not worth
having; a simpler design that is easier to trust usually is.

A spec-driven monorepo: **one monitor, several shells.** Log in to Mi Cloud, pick a camera, get its
live video + audio — with the audio (and a configurable crying alarm) kept alive in the background.
Today it ships on **Android** (Kotlin + Compose), **iOS** (Swift + SwiftUI), **macOS** (Swift +
AppKit) and **Windows** (C# + WinUI 3), and the architecture exists so that these are shells and
nothing more.

**Windows is the exception that proves the rule.** It cannot consume the Kotlin Multiplatform core,
so the monitor is *written twice*: `core/` in Kotlin, `windows/src/BabyMonitor.Core/` in C#. Two
implementations of one baby monitor is a genuine risk — a disagreement here is a missed cry — so the
port is not held together by care, it is held together by evidence: **the same interop vectors** pin
the wire byte for byte, and **the same spec suite** runs against both, criterion for criterion. If
you change behaviour, change the spec, then change *both* suites.

## Spec-driven workflow — READ FIRST

Specs are the **source of truth**. They live in `spec/` and describe **behavior only**.

**The process for any change (never skip a step):**

1. **Change the spec** (`spec/app.spec.md` or `spec/features/<feature>.spec.md`).
2. **Write failing tests** that map to the spec's acceptance criteria.
3. **Run them and confirm they fail** for the right reason.
4. **Implement** until tests pass.
5. **Confirm** `./gradlew check` is green and the result matches the spec.

**Rules (hard constraints):**

- Specs always represent the code. If code and spec disagree, one of them is a bug — fix it.
- Specs describe **what**, never **how**. No design, tech choices, file names, or implementation
  details in specs. (The Xiaomi protocol spec is the one deliberate exception: the wire format is
  externally imposed behavior, so it is specified byte-for-byte.)
- Keep specs as short as possible and to the point — but state every important detail.
- **Every acceptance criterion maps to at least one test.** Give criteria stable IDs (e.g.
  `ALRM-3`) and reference the ID in the test name so the mapping is checkable. Criteria that can
  only be observed on real hardware (background playback, lock-screen behavior) are marked
  `[device]` and map to `spec/device-checklist.md` instead of a unit test.
- Tests reflect **only** behavior written in specs. No speculative or inferred behavior.
- If behavior is removed from a spec, remove the corresponding tests (and code).

### Multi-platform specs

**One spec tree, split by feature — never by module.** A spec per module would mirror
implementation structure, and specs describe *what*, never *how*. There is deliberately no "core
spec": the core has no user-visible behavior of its own. Its behavior *is* the feature specs.

**Tag preference — always reach for the least specific tag that is still true:**

1. **Untagged** — holds on every platform. **Most criteria are this.**
2. **`[mobile]`** (both phones) or **`[desktop]`** (both desktops) — a behavior one *kind* of machine
   shares that the other does differently or not at all. *Some* criteria are this. Name the diverging
   nouns inline (notification / Live Activity; menu bar / tray).
3. **`[android]` / `[ios]` / `[macos]` / `[windows]`** — one platform only. **Avoid unless the
   *behavior* genuinely can't be shared** — a real capability gap, never an implementation detail. If
   two of these say the same thing, they were one `[mobile]` or `[desktop]` criterion all along.

(`[device]` is orthogonal: it marks "verifiable only on real hardware" and combines with any of the
above.) The rest of this section is the reasoning behind that order.

- **Untagged criteria are universal**, and their tests live in `core`'s common test source set — so
  they run on the JVM *and* on Kotlin/Native (macOS and the iOS simulator) — **and again** in the
  Windows port's suite (`windows/tests/`). "The apps behave the same" is executed four times, not
  asserted in prose. This is the whole reason the spec is shared.
- **`[mobile]` means Android *and* iOS; `[desktop]` means macOS *and* Windows** — the common case for
  anything one kind of machine can do and the other cannot. Two phones are one phone to a parent; a
  Mac and a PC are one desktop — a screen you work at, that sleeps, with a status area in the corner.
  Each kind gets one shell, written once (`desktop-shell.spec.md`; iOS's deltas in `ios-shell.spec.md`).
  Where the nouns differ — menu bar / tray, Quit / Exit, ⌘ / Ctrl — the criterion names both.
- **`[android]` / `[ios]` / `[macos]` / `[windows]`** tag a criterion that holds on **one** platform
  only, and they have to be earned: a real difference in *behavior*, not in implementation. Its test
  lives in that platform's source set (or the device checklist). **If a `[macos]` and a `[windows]`
  criterion say the same thing in different words, they were one `[desktop]` criterion all along** —
  likewise an `[android]` and an `[ios]` that agree are `[mobile]`. That is how twin criteria collapse.
- **An ID's prefix names a feature, never a platform** — no `MACOS-4`, no `WIN-4` (the feature
  prefix plus a tag carries it), and a **shared** behavior is never split into platform twins
  (`BG-11m`/`BG-11w`) — that is one `[mobile]`/`[desktop]` criterion. The **one** sanctioned platform
  marker in an ID is the **`i` suffix, for iOS**, and only on a capability-gap sibling: when the two
  phones answer one hazard with *different* behavior, the iOS twin takes the Android criterion's
  number + `i` (`BG-9` Android, `BG-9i` iOS), so the mirrored pair reads as a pair at a glance. It
  marks a genuine gap between the phones, never a shared behavior; desktop siblings, being a separate
  shell, keep their own fresh numbers instead. IDs are stable, so a rename is a cost paid across every
  test and comment that cites one.
- A platform-only *surface* gets its own feature spec rather than turning a shared spec into "on
  Android X, on macOS Y" soup. **Smell test:** if most of a feature spec's criteria are tagged, it is
  not one feature — it is two, and it should split.

**A capability gap is behavior, not an omission.** When one platform can do something another
cannot, the weaker platform does not go quiet about it. Every guarantee the app makes on one
platform but cannot make on another must have an explicit criterion on the weaker platform saying
**what the user is told instead**. Map hazard to hazard, not feature to feature: "warn when not
exempt from battery optimisation" (BG-9) is Android-shaped, but its hazard — *the OS quietly
suspends the monitor overnight* — exists on a desktop and on an iPhone too, so the desktops have
BG-12 and iOS BG-9i for the same hazard. The tags say who: **`[mobile]`** is both phones (Android +
iOS), **`[desktop]`** is macOS and Windows, and `[android]` / `[ios]` / `[macos]` / `[windows]` are
one platform only, each with a hazard-mapped sibling. A missing capability a user could mistake for a
working one is a bug, not a gap.

## Commands

Gradle is the task runner for the shared core and the Android app; the macOS and iOS apps build with
`swiftc` (Xcode's toolchain, no Xcode project — see each shell's `build.sh`).

| Task | Command |
| --- | --- |
| Everything (lint + tests, **every platform**) | `./gradlew check` |
| Core tests on the JVM | `./gradlew :core:testDebugUnitTest` |
| Core tests on Kotlin/Native (macOS) | `./gradlew :core:macosArm64Test` |
| Core tests on Kotlin/Native (iOS simulator) | `./gradlew :core:iosSimulatorArm64Test` |
| Build debug APK | `./gradlew :android:assembleDebug` |
| Build + install + launch on **emulator** | `./gradlew runEmulator` |
| Build + install + launch on **connected phone** | `./gradlew runPhone` |
| Build the macOS app | `./macos/build.sh [debug\|release]` |
| Run the macOS app | `./macos/run.sh` |
| Build the iOS app (Simulator) | `./ios/build.sh [debug\|release]` |
| Build + install + launch on a **simulator** | `./ios/run.sh [debug\|release] ["iPhone 17 Pro"]` |
| Look at a macOS screen without a camera | `BM_UI_PREVIEW=viewer ./macos/build/BabyMonitor.app/Contents/MacOS/BabyMonitor` |
| **The Windows monitor's spec suite** (runs anywhere) | `dotnet test windows/tests/BabyMonitor.Core.Tests/BabyMonitor.Core.Tests.csproj` |
| Build + run the Windows app (on Windows) | `.\windows\build.ps1 -Run` |
| Look at an iOS screen without a camera | `SIMCTL_CHILD_BM_UI_PREVIEW=viewer xcrun simctl launch <udid> com.bluzi.babymonitor` |

`./gradlew check` must be green before anything is considered done. It runs the core's tests on the
JVM **and** on every native target — macOS *and* the iOS simulator — so a change that breaks the
monitor on one platform alone cannot go green, which is the point.

**The Windows suite is the other half of that sentence**, and it needs no Windows: `windows/`'s core
is plain `net8.0` with no Windows API in it, so `dotnet test` runs it on a Mac, on Linux, anywhere.
Run it alongside `./gradlew check` whenever you touch shared behaviour — a change that breaks the
monitor on the PC alone must not be able to go green either.

`runEmulator` / `runPhone` target the device via `adb -e` / `adb -d`, so both can be connected at
once. Start an emulator first with `emulator -avd <name>` (or through Argent tools).

Building for macOS needs `brew install opus` (the camera speaks Opus and no Apple framework
decodes it; Homebrew's static `libopus.a` is linked into the binary). **iOS needs its own opus** —
Homebrew's is macOS-arm64 only, and the simulator and the device are their own platforms the linker
will not mix — so `./core/opus/build-ios-opus.sh` cross-compiles static `libopus.a` for both once
(cached under `core/build/opus-ios`; `ios/build.sh` runs it automatically if missing). There is no
Xcode project for either, on purpose — everything hard is in the shared core, and `swiftc` plus a
bundle layout is the whole build, which also means CI does not have to parse a `.pbxproj`. The iOS
app is verified on the **Simulator**; a real-device / App Store build (signing, provisioning, App
Store Connect) is a separate pipeline, and iOS updates are the App Store's job by design (UPD-2i).

### macOS signing — not cosmetic

`macos/build.sh` signs with the **Developer ID** certificate and embeds
`macos/Resources/BabyMonitor.provisionprofile`, which grants `keychain-access-groups`. That
combination is the only one that lets the app use the **data-protection Keychain**.

This matters far more than it sounds. The *login* Keychain guards an item against **the exact
binary that wrote it** — and every release is a different binary. So without the entitlement, macOS
asks for the login password before the app can read a session token it wrote itself, and that
prompt **blocks startup**: after an overnight auto-update you would find a monitor that is not
running and a password box nobody was awake to answer. A team-based signature does not help; the
login Keychain binds to the binary no matter who signed it (measured, twice). The data-protection
Keychain identifies the app by its *identity*, so an update is still the same app.

Claiming the entitlement **without** the profile makes the kernel SIGKILL the process at exec.
Certificate, profile and entitlement are a set; drop any one and it breaks, loudly or quietly.

`BM_SIGN=adhoc ./macos/build.sh` signs ad-hoc and never touches the Keychain. It exists for shells
that cannot answer a Keychain prompt (an agent, a CI runner): there, signing with a real identity
does not fail, it *blocks*, and the build never returns. Never use it for anything a person
installs — an ad-hoc build cannot read its own stored session without a password.

### Looking at the Mac UI without a camera

`BM_UI_PREVIEW=<login|devices|viewer|settings>` runs the shell against a **fake `UiState`** — no
Keychain, no camera, no monitor, and no writes to your real preferences. `BM_UI_ALARM`,
`BM_UI_STATUS`, `BM_UI_SHAPE=mini`, `BM_UI_HOVER`, `BM_UI_MUTED`, `BM_UI_OUTAGE`, `BM_UI_FEEDBACK`
pose the state you want to look at; `BM_UI_SNAPSHOT=/path.png` photographs the window and quits
(it works on a locked screen, where nothing outside the process can see a window at all). It is
dead code in a real run, and it is how the design in `spec/features/desktop-shell.spec.md` was
actually checked rather than assumed.

## Releasing

- **Every push to `main` is a release**, and the workflows are path-filtered: a change under
  `android/` releases the phone app, a change under `macos/` releases the Mac app, a change under
  `windows/` releases the PC app, and a change under `core/` releases the phone **and** the Mac —
  because it changed both. (`core/` does not release Windows: Windows carries its own port of the
  monitor. But `core/protocol-vectors.json` does, because it is the one file both ports are measured
  against.)
- Version is shared: `versionCode` = commit count on main, `versionName = 0.1.<versionCode>`.
- **Android:** signed APK published as GitHub Release `v0.1.<n>`. Keep **exactly one `.apk` asset
  per release** — Obtainium needs to resolve a single APK or silent background updates stop working.
  The Mac's and the PC's assets on the same release are harmless: Obtainium ignores every asset that
  does not end in `.apk`. And a desktop-only release, which carries no APK at all, is handled by
  Obtainium's **"Fallback to older releases"** setting (on by default) — it exists for exactly this
  case, and walks back to the last release that has an APK. If that setting is ever turned off, a
  desktop-only release would stall the phones on "no suitable release".
  Signing keystore: `~/keystores/c100-baby-monitor-release.jks` (never commit it; losing it means
  phones must uninstall/reinstall). CI signs via repo secrets `KEYSTORE_BASE64`,
  `KEYSTORE_PASSWORD`, `KEY_ALIAS`, injected as `BM_KEYSTORE_FILE` / `BM_KEYSTORE_PASSWORD` /
  `BM_KEY_ALIAS`.
- **macOS:** a zipped `.app` plus its SHA-256, published to the same release. The app updates
  itself (see `spec/features/updates.spec.md`).
- **Windows:** three assets, for two jobs — a **`-windows-setup.exe`** for the *first install only*,
  a **`-windows.zip`** which is what the *updater* consumes forever after, and their SHA-256s in
  **`checksums-windows.txt`**. That checksum file is its own, deliberately: the macOS and Windows jobs
  run at the same time, and appending to one shared `checksums.txt` would be a race whose loser ships
  a build with no checksum, which its own updater would then refuse to install (UPD-3).
  The setup is **per-user** (Inno Setup, `PrivilegesRequired=lowest`, into
  `%LOCALAPPDATA%\Programs\BabyMonitor`) and that is not a preference — it is what keeps the updater
  working. The app can rewrite that directory itself, so applying an update needs no elevation; in
  `Program Files` it would need an administrator, which means a UAC prompt at whatever hour the update
  lands, standing between a parent and a running monitor.
  Windows will not let a running program overwrite itself, so the swap is performed **by the new
  version**: the old one starts it with `--apply-update`, then exits.
- **The updater never restarts a running monitor.** It downloads, verifies, and waits for
  monitoring to stop. A monitor that relaunches itself at 3am is the failure this project exists to
  prevent — this is why we do not use Sparkle, whose model is "download and relaunch".

## Layout

```
spec/                 Source of truth (behavior). app.spec.md + features/*.spec.md
  device-checklist.md Manual verification for [device] criteria (Android + desktop, run on a Mac and a PC)

core/                 THE MONITOR. Kotlin Multiplatform: JVM (Android) + Kotlin/Native (macOS).
  commonMain/           xiaomi/  Mi Cloud + camera protocol, crypto (pure Kotlin), JSON shim
                        net/     MiHttp / TcpSocket / UdpSocket interfaces (impls per platform)
                        monitor/ Engine, reconnect, watchdog, level meter, cry alarm, HEVC parsing
                        dsp/     FFT, pitch, band energy
                        data/    Session / camera / settings persistence (logic only)
                        platform/ expect: secure random, monotonic clock, IO dispatcher
  androidMain/          java.net sockets + HttpURLConnection  (a jvm() target will reuse these)
  appleMain/            POSIX sockets, NSURLSession, libopus decode, AVAudioEngine  (iOS reuses)
  commonTest/           The spec suite. Runs on BOTH targets.
  protocol-vectors.json Interop vectors from the proven c100 TS impl; compiled into the tests

brand/                THE APP ICON, for every platform. icon.swift is the one description of the
                      mark; ./brand/build.sh renders it into macOS's .icns, Android's adaptive icon
                      AND Windows's .ico (plus the four tray states, which are the same mark).
                      Never export an icon per platform — that is how they stop matching.

android/              The Android shell: MediaCodec, AudioTrack, foreground service, Compose
macos/                The macOS shell: AppKit + SwiftUI, menu bar, VideoToolbox, Keychain, updater

windows/              THE PC. Its own core, because Windows cannot consume the Kotlin one.
  src/BabyMonitor.Core/    The monitor, in C#. net8.0 — NO Windows API in it, so it builds and its
                           tests run on any machine. Same packages as core/: xiaomi, net, monitor,
                           dsp, data, ui, shell.
  tests/BabyMonitor.Core.Tests/
                           The spec suite again, criterion for criterion, reading the SAME
                           core/protocol-vectors.json. This is what makes a second implementation
                           safe rather than reckless.
  src/BabyMonitor.App/     The shell: WinUI 3. Tray icon (Win32), one window / two shapes, WASAPI,
                           Media Foundation, DPAPI, the updater.
  Sources/
    AppDelegate.swift   Lifecycle, the menu bar item and its menu, updates, sleep/wake
    MainMenu.swift      The standard Mac menus — and therefore ⌘V (DESK-16)
    MonitorWindow.swift ONE window, two shapes (full / floating mini), and the morph between them
    RootView.swift      Routing + the video stage that both shapes share
    ViewerView.swift    The full shape: glass chrome over full-bleed video
    MiniView.swift      The mini shape: the floating tile's chrome
    Design.swift        Glass, controls, level bar, pointer tracking
    Preview.swift       Visual harness (BM_UI_PREVIEW=…) — dead code in a real run

ios/                  The iOS shell: SwiftUI, UIKit, VideoToolbox, Keychain, background audio,
                      Live Activity. Talks to core through the same `BabyMonitor` facade macOS uses.
  Sources/
    App.swift           @main App, the UIApplicationDelegate, and orientation lock (LIVE-9)
    AppState.swift      The thin observer over the facade + the iOS lifecycle (audio, network, LA)
    Audio.swift         AVAudioSession + the inaudible keep-alive that survives reconnects (BG-9i)
    Store.swift         KeyValueStore (UserDefaults) + SecretBox (Keychain)
    RootView.swift      Routing (APP-1); LoginView / CamerasView / ViewerView / SettingsView
    ViewerView.swift    Full-bleed video, glass chrome, tap-to-toggle, night vision, stop (BG-11)
    VideoLayerView.swift  AVSampleBufferDisplayLayer + VideoToolbox HEVC (LIVE-7)
    Design.swift        Liquid Glass surfaces, controls, level bar, banners
    Notifications.swift Local alarm notifications (ALRM-4/IOS-5); Haptics.swift is the vibrate
    LiveActivity.swift  Drives the monitoring Live Activity (BG-2/3)
    Preview.swift       Visual harness (BM_UI_PREVIEW=…) — dead code in a real run
  Shared/             MonitorActivity.swift: the ActivityAttributes + Stop intent, compiled into
                      both the app and the widget (the one type both sides must agree on)
  Widget/             The WidgetKit extension: lock-screen card + Dynamic Island (the .appex)
```

**The icon is shared, and generated (UI-3).** One mark, on the Mac, on both phones and on the PC, and
on whatever ships next: `brand/icon.swift` holds the colours and the geometry, and `./brand/build.sh`
renders `macos/Resources/AppIcon.icns`, Android's adaptive-icon vectors, iOS's `ios/Assets.xcassets`
app icon and Windows's `.ico` files from them in one run — the PC's icons are packed from the *same
pixels* the Mac's are. The outputs are committed, so a normal build never regenerates them — but they
are outputs. Editing `ic_launcher_foreground.xml` by hand, or exporting a new `.icns` from a design
tool, is how these platforms quietly stop being the same app. `./brand/build.sh --preview` draws what
each platform's mask will actually show, side by side. (Windows also needs `brew install imagemagick`.)

## Architecture conventions

- **The core is the app; a shell is a speaker, a screen and a buzzer.** Nothing in `core`'s
  `commonMain` may import `android.*` or platform APIs. The engine's whole platform edge is four
  interfaces — `AudioOutput`, `VideoOutput`, `Ringer`, and a `MediaFactory` that builds the first
  two — plus `net/`'s socket and HTTP seams and `platform/`'s clock and randomness. Adding a
  platform means implementing those and nothing else. If you find yourself wanting to widen this
  edge, that is a signal the logic belongs in core.
- **Every shell must feel native to its platform.** A shell is not a portable UI wearing four
  costumes — it is the platform's own idiom over the shared monitor. Use each platform's real
  controls, navigation, layout, typography, gestures and system integrations (the menu bar and
  Dock on a Mac, the tray and taskbar on a PC, Material and the back gesture on Android, the
  navigation stack, haptics and Live Activities on iOS), the way a parent who knows that platform
  expects them to work. Where platforms diverge, each shell does the *native* thing rather than a
  translation of another's — a Mac sheet is not an Android dialog, a PC's mini window sits where
  PC windows sit. This is a direct expression of ease of use (principle 2): an app that behaves
  like the device it is on is one a half-asleep parent can already use. Never copy one platform's
  UI onto another; give each the shell its users already know.
- **Contracts at that edge are load-bearing, not stylistic.** `AudioOutput` must keep decoding and
  keep feeding the analysis tap while muted (LIVE-3) — an implementation that mutes by pausing the
  decoder has silently disabled the crying alarm. `VideoOutput.push` must never throw (LIVE-7) —
  video trouble must never take audio down with it. These are commented at the interface; read them
  before implementing one.
- **One crypto implementation, proven.** MD5/SHA-1/SHA-256 and X25519 are pure Kotlin in core, so
  no two platforms can disagree about the wire. `CryptoDifferentialTest` runs them against
  `java.security.MessageDigest` and BouncyCastle over thousands of random inputs. Keep it: it is
  what caught SHA-1's initial state being one hex digit short — a bug the fixed interop vectors
  alone did not find.
- **Protocol fidelity beats cleverness.** The Xiaomi flow (login → signed RC4 API → NaCl shared
  key → CS2 handshake → MISS ChaCha20 media) is ported from the working implementation in
  `/Users/bluzi/repos/c100` (which itself ports go2rtc). When touching it, compare against
  `spec/features/xiaomi-protocol.spec.md` and the interop vectors; never "improve" wire behavior.
- **Session safety:** Mi account tokens are stored encrypted — Android Keystore on the phone, the
  Keychain on the Mac, DPAPI on the PC. The encryption primitive is behind an interface with a
  passthrough fake for tests; serialization is pure and fully tested.
- **The monitor owns itself.** All monitoring (connection loop, decode, level, alarm) runs
  independently of any UI and survives it — a foreground service on Android, the tray-resident
  process on macOS and Windows. The UI is a thin observer over `MonitorHub`'s state flows; closing
  and reopening it must never restart the stream.
- **The C# port is a mirror, not a rewrite.** `windows/src/BabyMonitor.Core/` is the Kotlin core
  translated file for file, with the same names, the same comments and the same decisions. Keep it
  that way: the value of a port you can read side by side with the original is that a reviewer can
  *see* they agree. When you change one, change the other, and change both suites. The one place they
  deliberately differ is the hashes — Kotlin/Native has no `MessageDigest`, so the Kotlin core ports
  MD5/SHA-1/SHA-256 by hand; .NET has had them for twenty years, and a hand-rolled hash next to a
  correct one in the box is a liability, not an asset. The interop vectors prove both.
- **English only, simple dark UI.** No i18n layer yet — user-facing copy lives in the views; keep
  it terse.

## Logging (permanent — do not remove)

The app logs its whole lifecycle so field issues are debuggable from a device. Keep and extend
this; don't strip it out.

- **Facade:** `core`'s `log/Log.kt` (and the port's `Logging/Log.cs`) is a platform-free logging
  facade so the protocol layer can log while staying portable. Each app installs a sink at process
  start (Android: `android.util.Log`; macOS: `os_log`; Windows: a file). Unit tests leave the default
  no-op sink. Never log secrets (password, passToken, serviceToken, ssecurity) — log ids, ips,
  statuses, and error messages.
- **One tag, per-subsystem labels.** Everything logs under `BabyMonitor`, with the subsystem in the
  message: `login`, `cloud`, `cs2`, `miss`, `engine`, `service`, `ui`, `video`, `audio`, `update`,
  `app`.
- **Read it live:** `adb -d logcat -s BabyMonitor` (phone),
  `log stream --predicate 'subsystem == "com.bluzi.babymonitor"'` (Mac), or
  `Get-Content -Wait $env:LOCALAPPDATA\BabyMonitor\babymonitor.log` (PC — there is no system log to
  lean on there, so the app keeps its own, capped and rolled once so a monitor left running for a
  month cannot fill a disk).
- **What's covered:** login (each step, captcha/2FA, redirect hops at DEBUG, token refresh,
  success/failure), signed cloud requests (path, non-200, error codes), device list, MiOT get/set,
  the CS2 handshake stages (LAN search → punch → P2P-ready → transport up) and connection loss,
  MISS auth + startMedia, the engine connection loop (connecting → LIVE, first audio/video frame,
  reconnect with backoff, watchdog), the monitor's lifecycle, sleep/wake on macOS, the updater, and
  a room-level summary line every 30 s while audio is live (median/max dB above ambient + the
  absolute noise floor — a quiet room must read "median 0.0"). When adding a subsystem, give it a
  tag and log its entry/exit and every error path.

## Gotchas that cost real debugging time

Inherited from the c100 project:

- Mi login responses are prefixed with `&&&START&&&` before the JSON.
- The signed-request cookie set must be exactly `userId`, `serviceToken` (+ `cUserId` if known) —
  any extra cookie makes the gateway reject the signature. Our own HTTP client (no cookie jar, no
  auto-redirects) exists precisely to control this. **On Apple this takes work**: NSURLSession has
  an ambient cookie store and follows redirects by default, and it must be told not to do either.
  Both failures look exactly like an expired session.
- `NSHTTPURLResponse.allHeaderFields` **merges duplicate headers**, including `Set-Cookie`, of
  which the Mi auth chain sends several per hop. Splitting on commas is wrong (cookie `Expires`
  values contain commas). Use `NSHTTPCookie.cookiesWithResponseHeaderFields`, which knows the
  grammar.
- `ssecurity` may arrive only in the `Extension-Pragma` header of a redirect hop — a new
  `ssecurity` is always paired with a new `serviceToken`; never mix old/new.
- CS2 keepalive: ping every ~1 s on an independent timer, and **never** reply PONG to the camera's
  PING — Mi Home doesn't, and cameras drop the session if you do.
- RC4 here discards the first 1024 keystream bytes; ChaCha20 nonces are 8 bytes on the wire,
  left-padded with 4 zero bytes.

From this repo:

- **The repository is public, and the updater sends no credential — on purpose.** Every device
  updates itself out of the box with nothing to set up (UPD spec). A release asset still answers with
  a 302 to GitHub's CDN, but with no `Authorization` header to forward there is nothing to strip, so
  both shells just follow the redirect (`URLSession` does it for free; the C# side follows manually
  only to give each hop its own inactivity timeout). **Do not re-add an `Authorization` header to the
  updater**: the moment it carries a bearer token across the CDN redirect, the CDN rejects the request
  outright ("Only one auth mechanism allowed") — which is the bug the private-repo version had to work
  around, and which going public removed.
- **The macOS SDK is part of what ships, not a property of the build machine.** A Mac app adopts the
  system's current design system only if it was **compiled against the current SDK**. Build it on an
  older one and it compiles, runs, and comes out wearing the previous decade's look on a brand-new
  Mac — with nothing in the log to say so. The release workflow ran on `macos-14` (SDK 14.5) and
  would have shipped exactly that. It runs on `macos-26` now, and `macos/build.sh` **refuses to
  build** on an SDK older than 26 rather than quietly produce a different app.
- **A Mac debug build does not prove a Mac release build.** `-Onone` *warns* where `-O` **errors** —
  notably "reference to captured var 'self' in concurrently-executing code", which is what you get
  by reading an outer closure's `[weak self]` from inside a nested `Task` (capture it again:
  `Task { @MainActor [weak self] in … }`). The app ran perfectly here and the release workflow could
  not compile it at all. **`./macos/build.sh release` before pushing anything that touches
  `macos/`** — it is the only local build that proves the thing CI ships.
- **The iOS Simulator cannot use the Keychain, and no amount of signing fixes it.** The iOS 26
  Simulator's AMFI SIGKILLs an ad-hoc-signed app at exec the instant it carries *any* restricted
  entitlement — `keychain-access-groups` included — so the launch is denied ("denied by service
  delegate (SBMainWorkspace)", and no crash report, because the kernel kills it before `main()`).
  Signing with a real Apple Development identity does not change it without a provisioning profile
  (the separate device pipeline). So the Simulator build carries no keychain entitlement, and with no
  keychain-access-group `SecItemAdd` returns `errSecMissingEntitlement` (-34018): the session cannot
  be persisted on the Simulator. From the UI this looked like login silently bouncing back to the
  email/password screen — on a real device AUTH-13 now makes the app say so instead. It never showed
  in `BM_UI_PREVIEW` runs, where `seal()` early-returns before touching the Keychain. To make a real
  sign-in → cameras → viewer flow testable on the Simulator anyway, `KeychainSecretBox` has a
  **dev-only UserDefaults fallback** behind `#if targetEnvironment(simulator)` (in
  `ios/Sources/Store.swift`) — it stands in for the Keychain there and is compiled out of every device
  build, so it can never ship. Real, provisioned devices keep the session in the Keychain.
- **`Dispatchers.IO` is not public API on Kotlin/Native.** The monitor's socket reads block, so
  `appleMain` runs them on a dedicated thread pool; putting them on `Dispatchers.Default` would let
  a couple of stalled reads starve the watchdog tick that is supposed to notice the stall.
- **Kotlin/Native forbids commas in backticked test names** (the JVM allows them). Test names carry
  spec IDs, so they are long — write them without commas.
- `runTest`'s virtual clock advances the instant the test coroutine idles, which fires every
  `withTimeout` before a *real* worker thread can deliver. Tests that drive real concurrency (the
  CS2 transport, the sockets) use `runRealTimeTest`.

Windows:

- **A Windows UDP socket resets itself when a peer is briefly unreachable, and it looks exactly like
  a camera that never answers.** Windows raises a peer's ICMP "port unreachable" as `WSAECONNRESET`
  on the socket's *next* receive — so the CS2 LAN-search datagram, sent to a camera that has not yet
  opened its handshake responder, poisons the very next read and the handshake aborts. The reconnect
  loop then does the same thing forever: the PC never connects while the Mac and the phone do, because
  POSIX sockets never surface this. The fix is the canonical `SIO_UDP_CONNRESET` disable on the
  Windows UDP socket (`SystemUdpSocket`, guarded to Windows — the ioctl is rejected elsewhere, and
  this core's tests run on a Mac), plus the portable belt-and-braces of PROTO-25: the handshake
  tolerates a transient read failure instead of treating it as a dead connection.
- **Media Foundation will not decode a byte without a frame size.** MediaCodec and VideoToolbox work
  the size out from the parameter sets; a Windows media type must carry it up front. So the C# core
  parses the SPS (`HevcSps.Dimensions`) — Exp-Golomb, emulation-prevention bytes and the conformance
  window (a "1080p" stream codes 1088 rows and crops 8). It is also where the window gets the
  camera's shape (DESK-12), so it earns its keep twice.
- **The camera sends VPS/SPS/PPS in their own access units**, so the Windows renderer prepends them
  to every keyframe. A decoder that started late has nothing to start from otherwise.
- **Windows may have no H.265 decoder at all** (the HEVC Video Extensions are a separate free Store
  download). That is a capability gap, so it is *behaviour*: the app says so, points at the
  extension, and keeps monitoring (DESK-22). It must never be a black rectangle.
- **DPAPI keys on the user, not the binary** — which is the whole reason the session uses it. An
  update replaces every byte of the app and the stored session still opens with no prompt (AUTH-12).
  The Mac needs a Developer ID, a provisioning profile and an entitlement to buy the same thing.
- **Windows will not let a running program overwrite itself.** The updater therefore hands the job to
  the *new* build (`--apply-update <dir> <pid>`), which waits for the old one to exit, swaps the
  files and starts it again.
- **`IsEnabled` is on `Control`, not on `Panel`.** A `StackPanel` cannot be greyed out; wrap the
  group in a `ContentControl` (this is why `AlarmGroup` is one).
- **A WinUI 3 app cannot be built on a Mac** — the XAML compiler is a Windows binary. But everything
  *else* can: the core and its whole spec suite are plain `net8.0`, and the shell's C# can be
  type-checked against the real WinUI assemblies by compiling it with `EnableWindowsTargeting=true`
  and stubbed `InitializeComponent`s. Two real bugs were caught that way before the code ever met a
  PC. The markup itself still needs a Windows build to prove — and it must actually be *run*, because:
- **A WinUI 3 *publish* silently drops the app's own resources, and then the app never opens.** The
  unpackaged, self-contained `msbuild /t:Publish` copies the *framework* PRIs (`Microsoft.UI.pri`, …)
  but not the app's own `<AssemblyName>.pri` or its compiled XAML (`App.xbf`, `MainWindow.xbf`,
  `SettingsWindow.xbf`). The zipped/installed folder then has no `ms-appx:///…xaml`, so the first line
  of `MainWindow`'s constructor — `InitializeComponent()` — throws `XamlParseException` ("Cannot locate
  resource from 'ms-appx:///MainWindow.xaml'"), and `App.OnUnhandledException` dutifully swallows it
  (DESK-6), leaving a live process with **no window**: the monitor that never appears. A plain `build`
  output runs fine; only `publish` is broken — so it stayed invisible until a real PC ran the shipped
  zip. `BabyMonitor.App.csproj`'s `_IncludeAppXamlResourcesInPublish` target adds them back into
  `@(ResolvedFileToPublish)`. This is *the* reason a Windows build must be launched, not just compiled.
- **A stale `.pri` breaks the app exactly like a missing one — and an incremental build makes stale.**
  The PRI task decides it is up to date from the *set* of resource files, and editing a `.xaml` does not
  change the set. So `build.ps1` recompiles `MainWindow.xaml` into a fresh `.xbf` and leaves yesterday's
  `BabyMonitor.pri` next to it; the two disagree, `ms-appx:///MainWindow.xaml` will not resolve, and you
  get the same `XamlParseException` → swallowed by `OnUnhandledException` (DESK-6) → **live process, no
  window** as the publish bug below. It is worse than that one, because a clean build fixes it and CI is
  always clean, so it can only ever bite a person at their desk — and it looks like whatever you just
  edited. `build.ps1` now deletes the index before every publish. If you ever see `XamlParseException`
  from `InitializeComponent`, suspect the index before the markup.
- **Windows Firewall drops the camera's answer, and only the PC has this problem** (DESK-24). The CS2
  handshake is not a connection *to* the camera: it asks the camera to punch back, and the camera answers
  from an **ephemeral port of its own** (19775, 21044, 22379 — a different one every time), never from
  the `:32108` we sent to. Windows Firewall never sent anything to that port, so on a **Public** network
  it drops the reply as unsolicited; the handshake dies at LAN search and the monitor reconnects for ever
  behind a tidy countdown. A Mac and a phone have no such filter, which is why the same core connects
  there and not here, and why this reads as "the PC build is broken" when nothing is. The installer asks
  once for an inbound rule; if it is refused the app says so itself rather than counting attempts. When
  debugging this, `Test-NetConnection … -Port 32108` proves nothing — it is TCP, and the handshake is UDP.
- **The C100 serves a limited number of P2P sessions.** With the phone app monitoring, a PC connects,
  authenticates, calls `startMedia` — and the camera simply never streams: no audio, no video, and the
  watchdog drops it after 10 s. It looks exactly like a broken media path. Close the other clients before
  concluding anything about the code.
- **Build the app with Visual Studio's MSBuild, never `dotnet publish`** (both `build.ps1` and CI do).
  The Windows App SDK generates the PRI with an MSBuild task (`Microsoft.Build.Packaging.Pri.Tasks`)
  that ships in VS's MSBuild, not the .NET SDK's; under `dotnet` it is sought in the SDK tree, missing,
  and the build dies (MSB4062). Finding VS's MSBuild via `vswhere` needs care: pass `-all` (a VS
  instance can sit in a state the default query hides, e.g. just after a workload was added) and
  `-version '[17.0,)'` (VS 2019's MSBuild cannot build .NET 8), and do **not** filter by
  `-requires Microsoft.Component.MSBuild` (a Community install may not advertise it).
