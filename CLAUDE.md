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
Today it ships on **Android** (Kotlin + Compose) and **macOS** (Swift + AppKit). Windows and iOS are
expected to follow, and the architecture exists so that they are shells and nothing more.

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

- **Untagged criteria are universal**, and their tests live in `core`'s common test source set — so
  they run on the JVM *and* on Kotlin/Native. "Both apps behave the same" is executed twice, not
  asserted in prose. This is the whole reason the spec is shared.
- **`[android]` / `[macos]`** tag a criterion that genuinely differs in *behavior*, not in
  implementation. Its test lives in that platform's source set (or the device checklist).
- A whole platform-only *surface* gets its own feature spec (`macos-shell.spec.md`), rather than
  turning a shared spec into "on Android X, on macOS Y" soup. **Smell test:** if most of a feature
  spec's criteria are tagged, it is not one feature — it is two, and it should split.

**A capability gap is behavior, not an omission.** When one platform can do something another
cannot, the weaker platform does not go quiet about it. Every guarantee the app makes on one
platform but cannot make on another must have an explicit criterion on the weaker platform saying
**what the user is told instead**. Map hazard to hazard, not feature to feature: "warn when not
exempt from battery optimisation" (BG-9) is Android-shaped, but its hazard — *the OS quietly
suspends the monitor overnight* — exists on a Mac too, so the Mac has BG-12 for the same hazard.
A missing capability a user could mistake for a working one is a bug, not a gap.

## Commands

Gradle is the task runner for the shared core and the Android app; the macOS app builds with Xcode.

| Task | Command |
| --- | --- |
| Everything (lint + tests, **both platforms**) | `./gradlew check` |
| Core tests on the JVM | `./gradlew :core:testDebugUnitTest` |
| Core tests on Kotlin/Native (macOS) | `./gradlew :core:macosArm64Test` |
| Build debug APK | `./gradlew :android:assembleDebug` |
| Build + install + launch on **emulator** | `./gradlew runEmulator` |
| Build + install + launch on **connected phone** | `./gradlew runPhone` |
| Build the macOS app | `./macos/build.sh [debug\|release]` |
| Run the macOS app | `./macos/run.sh` |
| Look at a macOS screen without a camera | `BM_UI_PREVIEW=viewer ./macos/build/BabyMonitor.app/Contents/MacOS/BabyMonitor` |

`./gradlew check` must be green before anything is considered done. It runs the core's tests on
**both** the JVM and Kotlin/Native — a change that breaks the monitor on macOS alone cannot go
green, which is the point.

`runEmulator` / `runPhone` target the device via `adb -e` / `adb -d`, so both can be connected at
once. Start an emulator first with `emulator -avd <name>` (or through Argent tools).

Building for macOS needs `brew install opus` (the camera speaks Opus and no Apple framework
decodes it; Homebrew's static `libopus.a` is linked into the binary). There is no Xcode project on
purpose — the app is a menu bar item, a few windows and a picture, and everything hard is in the
shared core. `swiftc` plus a bundle layout is the whole build, which also means CI does not have to
parse a `.pbxproj`.

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
dead code in a real run, and it is how the design in `spec/features/macos-shell.spec.md` was
actually checked rather than assumed.

## Releasing

- **Every push to `main` is a release**, and the workflows are path-filtered: a change under
  `android/` releases the phone app, a change under `macos/` releases the Mac app, and a change
  under `core/` releases **both** — because it changed both.
- Version is shared: `versionCode` = commit count on main, `versionName = 0.1.<versionCode>`.
- **Android:** signed APK published as GitHub Release `v0.1.<n>`. Keep **exactly one `.apk` asset
  per release** — Obtainium needs to resolve a single APK or silent background updates stop working.
  The Mac's `.dmg`/`.zip`/`checksums.txt` on the same release are harmless: Obtainium ignores every
  asset that does not end in `.apk`. And a macOS-only release, which carries no APK at all, is
  handled by Obtainium's **"Fallback to older releases"** setting (on by default) — it exists for
  exactly this case, and walks back to the last release that has an APK. If that setting is ever
  turned off, a macOS-only release would stall the phones on "no suitable release".
  Signing keystore: `~/keystores/c100-baby-monitor-release.jks` (never commit it; losing it means
  phones must uninstall/reinstall). CI signs via repo secrets `KEYSTORE_BASE64`,
  `KEYSTORE_PASSWORD`, `KEY_ALIAS`, injected as `BM_KEYSTORE_FILE` / `BM_KEYSTORE_PASSWORD` /
  `BM_KEY_ALIAS`.
- **macOS:** a zipped `.app` plus its SHA-256, published to the same release. The app updates
  itself (see `spec/features/updates.spec.md`).
- **The updater never restarts a running monitor.** It downloads, verifies, and waits for
  monitoring to stop. A monitor that relaunches itself at 3am is the failure this project exists to
  prevent — this is why we do not use Sparkle, whose model is "download and relaunch".

## Layout

```
spec/                 Source of truth (behavior). app.spec.md + features/*.spec.md
  device-checklist.md Manual verification for [device] criteria (Android + macOS sections)

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
                      mark; ./brand/build.sh renders it into macOS's .icns AND Android's adaptive
                      icon. Never export an icon per platform — that is how they stop matching.

android/              The Android shell: MediaCodec, AudioTrack, foreground service, Compose
macos/                The macOS shell: AppKit + SwiftUI, menu bar, VideoToolbox, Keychain, updater
  Sources/
    AppDelegate.swift   Lifecycle, the menu bar item and its menu, updates, sleep/wake
    MainMenu.swift      The standard Mac menus — and therefore ⌘V (MACOS-13)
    MonitorWindow.swift ONE window, two shapes (full / floating mini), and the morph between them
    RootView.swift      Routing + the video stage that both shapes share
    ViewerView.swift    The full shape: glass chrome over full-bleed video
    MiniView.swift      The mini shape: the floating tile's chrome
    Design.swift        Glass, controls, level bar, pointer tracking
    Preview.swift       Visual harness (BM_UI_PREVIEW=…) — dead code in a real run
```

**The icon is shared, and generated (UI-3).** One mark, on the Mac and on the phone, and on
whatever ships next: `brand/icon.swift` holds the colours and the geometry, and `./brand/build.sh`
renders *both* `macos/Resources/AppIcon.icns` and Android's adaptive-icon vectors from them in one
run. The outputs are committed, so a normal build never regenerates them — but they are outputs.
Editing `ic_launcher_foreground.xml` by hand, or exporting a new `.icns` from a design tool, is how
the two platforms quietly stop being the same app. `./brand/build.sh --preview` draws what each
platform's mask will actually show, side by side.

## Architecture conventions

- **The core is the app; a shell is a speaker, a screen and a buzzer.** Nothing in `core`'s
  `commonMain` may import `android.*` or platform APIs. The engine's whole platform edge is four
  interfaces — `AudioOutput`, `VideoOutput`, `Ringer`, and a `MediaFactory` that builds the first
  two — plus `net/`'s socket and HTTP seams and `platform/`'s clock and randomness. Adding a
  platform means implementing those and nothing else. If you find yourself wanting to widen this
  edge, that is a signal the logic belongs in core.
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
  Keychain on the Mac. The encryption primitive is behind an interface with a passthrough fake for
  tests; serialization is pure and fully tested.
- **The monitor owns itself.** All monitoring (connection loop, decode, level, alarm) runs
  independently of any UI and survives it — a foreground service on Android, the tray-resident
  process on macOS. The UI is a thin observer over `MonitorHub`'s state flows; closing and
  reopening it must never restart the stream.
- **English only, simple dark UI.** No i18n layer yet — user-facing copy lives in the views; keep
  it terse.

## Logging (permanent — do not remove)

The app logs its whole lifecycle so field issues are debuggable from a device. Keep and extend
this; don't strip it out.

- **Facade:** `core`'s `log/Log.kt` is a platform-free logging facade so the protocol layer can log
  while staying portable. Each app installs a sink at process start (Android: `android.util.Log`;
  macOS: `os_log`). Unit tests leave the default no-op sink. Never log secrets (password,
  passToken, serviceToken, ssecurity) — log ids, ips, statuses, and error messages.
- **One tag, per-subsystem labels.** Everything logs under `BabyMonitor`, with the subsystem in the
  message: `login`, `cloud`, `cs2`, `miss`, `engine`, `service`, `ui`, `video`, `audio`, `update`,
  `app`.
- **Read it live:** `adb -d logcat -s BabyMonitor` (phone) or
  `log stream --predicate 'subsystem == "com.bluzi.babymonitor"'` (Mac).
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

- **GitHub release assets on a private repo redirect to S3, and S3 rejects the request if our
  `Authorization` header comes with it** ("Only one auth mechanism allowed"). The updater must
  **strip `Authorization` on a cross-host redirect** — in `URLSession` that means implementing
  `willPerformHTTPRedirection` and returning a request with the header removed.
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
- **`Dispatchers.IO` is not public API on Kotlin/Native.** The monitor's socket reads block, so
  `appleMain` runs them on a dedicated thread pool; putting them on `Dispatchers.Default` would let
  a couple of stalled reads starve the watchdog tick that is supposed to notice the stall.
- **Kotlin/Native forbids commas in backticked test names** (the JVM allows them). Test names carry
  spec IDs, so they are long — write them without commas.
- `runTest`'s virtual clock advances the instant the test coroutine idles, which fires every
  `withTimeout` before a *real* worker thread can deliver. Tests that drive real concurrency (the
  CS2 transport, the sockets) use `runRealTimeTest`.
