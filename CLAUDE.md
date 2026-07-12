# C100 Baby Monitor (Android)

An Android app that turns Xiaomi cameras into a baby monitor. **Three things matter, in this
order — keep them in mind in everything you do here:**

1. **Reliability.** It should always work, and it must say so when it stops working. Silence must
   never be mistaken for a calm baby. We are in charge of babies' lives.
2. **Ease of use.** Good UX, easy to use — including at 3am, half-asleep, in the dark.
3. **Simplicity.** It is what makes the other two possible: simple code fails less, and a simple
   app is easier to use.

When these conflict, the earlier one wins. A clever feature that risks a missed cry is not worth
having; a simpler design that is easier to trust usually is.

A spec-driven **native Android app** (Kotlin + Jetpack Compose): log in to Mi Cloud, pick a
camera, and get its live video + audio — with the audio (and a configurable crying alarm) kept
alive in the background, screen locked or off.

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
  only be observed on a real device (background playback, lock-screen behavior) are marked
  `[device]` in the spec and map to the manual device checklist in `spec/device-checklist.md`
  instead of a unit test.
- Tests reflect **only** behavior written in specs. No speculative or inferred behavior.
- If behavior is removed from a spec, remove the corresponding tests (and code).

## Commands

Gradle is the task runner (no Makefile — this is the Android convention).

| Task | Command |
| --- | --- |
| Build debug APK | `./gradlew assembleDebug` |
| Build + install + launch on **emulator** | `./gradlew runEmulator` |
| Build + install + launch on **connected phone** | `./gradlew runPhone` |
| Unit tests | `./gradlew testDebugUnitTest` |
| Everything (lint + tests) | `./gradlew check` |
| Lint only | `./gradlew lintDebug` |
| Release build | `./gradlew assembleRelease` |

`runEmulator` / `runPhone` target the device via `adb -e` / `adb -d`, so both can be connected at
once. Start an emulator first with `emulator -avd <name>` (or through Argent tools).

`./gradlew check` must be green before anything is considered done.

## Releasing (OTA via Obtainium)

- **Every push to `main` is a release.** `.github/workflows/release.yml` runs `check`, builds a
  signed release APK (`versionCode` = commit count on main, `versionName = 0.1.<versionCode>`),
  and publishes it as GitHub Release `v0.1.<versionCode>`. Both phones run Obtainium pointed at
  this repo and install updates themselves — so never push broken code to main.
- Keep **exactly one `.apk` asset per release** — Obtainium needs to resolve a single APK or
  silent background updates stop working.
- Signing keystore: `~/keystores/c100-baby-monitor-release.jks` (credentials file alongside —
  never commit either; losing the keystore means phones must uninstall/reinstall). CI signs via
  repo secrets `KEYSTORE_BASE64`, `KEYSTORE_PASSWORD`, `KEY_ALIAS`, injected as the
  `BM_KEYSTORE_FILE` / `BM_KEYSTORE_PASSWORD` / `BM_KEY_ALIAS` environment variables.

## Layout

```
spec/                 Source of truth (behavior). app.spec.md + features/*.spec.md
  device-checklist.md Manual verification steps for [device] criteria
app/src/main/java/com/bluzi/babymonitor/
  xiaomi/             Mi Cloud + camera protocol (pure Kotlin/JVM — no android.* imports)
  net/                Socket + HTTP abstractions; real impls injected at the edge
  monitor/            Monitoring engine: service, decoders, level meter, crying alarm, watchdog
  dsp/                Pure DSP helpers (FFT, band energy)
  data/               Persistence (session, selected camera, settings)
  ui/                 Compose screens (login, devices, viewer, settings)
app/src/test/         JVM unit tests, one file per spec area, test names carry spec IDs
  resources/protocol-vectors.json   Interop vectors generated from the proven c100 TS impl
```

## Architecture conventions

- **The protocol layer is pure JVM.** Nothing under `xiaomi/`, `dsp/`, or the logic parts of
  `monitor/` and `data/` may import `android.*`. All I/O (HTTP, TCP/UDP sockets), time, and
  randomness are injected via the interfaces in `net/` (`MiHttp`, `TcpSocket`, `UdpSocket`) and
  plain `() -> Long` clocks — that is what makes the spec tests possible. Android-only code
  (MediaCodec, AudioTrack, Service, Keystore, Compose) lives at the edges.
- **Protocol fidelity beats cleverness.** The Xiaomi flow (login → signed RC4 API → NaCl shared
  key → CS2 handshake → MISS ChaCha20 media) is ported from the working implementation in
  `/Users/bluzi/repos/c100` (which itself ports go2rtc). When touching it, compare against
  `spec/features/xiaomi-protocol.spec.md` and the interop vectors; never "improve" wire behavior.
- **Session safety:** Mi account tokens are stored encrypted via Android Keystore. The encryption
  primitive is behind an interface with a passthrough fake for tests; serialization is pure and
  fully tested.
- **The service is the app.** All monitoring (connection loop, decode, level, alarm) runs in the
  foreground service and survives the Activity. The UI is a thin observer over shared state flows;
  closing/reopening the UI must never restart the stream.
- **English only, simple dark UI.** No i18n layer yet — user-facing copy lives in the composables;
  keep it terse.

## Logging (permanent — do not remove)

The app logs its whole lifecycle so field issues are debuggable from a device. Keep and extend
this; don't strip it out.

- **Facade:** `log/Log.kt` is a pure (no `android.*`) logging facade so the protocol layer can log
  while staying platform-free. `BabyMonitorApp` installs a sink backed by `android.util.Log` at
  process start. Unit tests leave the default no-op sink. Never log secrets (password, passToken,
  serviceToken, ssecurity) — log ids, ips, statuses, and error messages.
- **One Android tag, per-subsystem labels.** Everything logs under the Android tag `BabyMonitor`,
  with the subsystem in the message: `login`, `cloud`, `cs2`, `miss`, `engine`, `service`, `ui`,
  `video`, `app`.
- **Read it live:** `adb -d logcat -s BabyMonitor` (phone) or `adb -e logcat -s BabyMonitor`
  (emulator). Filter a subsystem with e.g. `adb logcat -s BabyMonitor | grep "\[cs2\]"`.
- **What's covered:** login (each step, captcha/2FA, redirect hops at DEBUG, token refresh,
  success/failure), signed cloud requests (path, non-200, error codes), device list, MiOT
  get/set, the CS2 handshake stages (LAN search → punch → P2P-ready → transport up) and connection
  loss, MISS auth + startMedia, the engine connection loop (connecting → LIVE, first audio/video
  frame, reconnect with backoff, watchdog), the service lifecycle, and a room-level summary line
  every 30 s while audio is live (median/max dB above ambient + the absolute noise floor — a
  quiet room must read "median 0.0"). When adding a subsystem, give it a tag and log its
  entry/exit and every error path.

## Gotchas that cost real debugging time (inherited from the c100 project)

- Mi login responses are prefixed with `&&&START&&&` before the JSON.
- The signed-request cookie set must be exactly `userId`, `serviceToken` (+ `cUserId` if known) —
  any extra cookie makes the gateway reject the signature. Our own HTTP client (no cookie jar,
  no auto-redirects) exists precisely to control this.
- `ssecurity` may arrive only in the `Extension-Pragma` header of a redirect hop — a new
  `ssecurity` is always paired with a new `serviceToken`; never mix old/new.
- CS2 keepalive: ping every ~1 s on an independent timer, and **never** reply PONG to the
  camera's PING — Mi Home doesn't, and cameras drop the session if you do.
- RC4 here discards the first 1024 keystream bytes; ChaCha20 nonces are 8 bytes on the wire,
  left-padded with 4 zero bytes.
