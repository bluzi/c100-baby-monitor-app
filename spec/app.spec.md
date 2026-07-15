# App spec

C100 Baby Monitor turns a Xiaomi C100 camera into a baby monitor: sign in to the Mi account once,
pick the camera once, and from then on the app shows the camera's live video and audio. Audio
monitoring — including the optional noise alarm — keeps running with the app in the background.

It runs on **Android** and **iOS** (a phone), **macOS** (a Mac, from the menu bar) and **Windows**
(a PC, from the tray). It is one monitor with several shells: the protocol, the connection engine,
the reconnect and watchdog logic, the cry detection and the alarm are the same behaviour, and the
criteria below hold on all of them.

Feature specs: [login](features/login.spec.md) ·
[camera-selection](features/camera-selection.spec.md) · [live-feed](features/live-feed.spec.md) ·
[background](features/background.spec.md) · [noise-alarm](features/noise-alarm.spec.md) ·
[stream-watchdog](features/stream-watchdog.spec.md) · [updates](features/updates.spec.md) ·
[ios-shell](features/ios-shell.spec.md) · [desktop-shell](features/desktop-shell.spec.md) ·
[xiaomi-protocol](features/xiaomi-protocol.spec.md)

## How to read a criterion

Criteria have stable IDs (`ALRM-3`). Every one maps to at least one test that names the ID. **An ID
never names a platform** — the tag does that, and only the tag. An ID is a name for a behaviour, and
a behaviour that moves from one platform to two must not have to be renamed to say so.

- **Untagged criteria are universal.** They hold on every platform, and their tests run on every
  platform: the shared core's suite executes on the JVM (Android) *and* on Kotlin/Native (macOS and
  the iOS simulator), and the Windows port runs the same suite again, criterion for criterion. "The
  apps behave the same" is not a hope; it is executed four times.
- **`[mobile]`** marks a criterion shared by **both phones** — Android and iOS — because they are
  phones, where a desktop does it differently or not at all. Prefer it: two phones that behave the
  same should say so once.
- **`[desktop]`** is its counterpart: **macOS and Windows, both**. A Mac and a PC are the same kind
  of machine to a parent — a screen you work at, that sleeps, with a status area in the corner — and
  they get the same monitor. The nouns differ (menu bar / tray, Quit / Exit) and the spec names
  both; the promise does not differ, so it is written once.
- **`[android]` / `[ios]` / `[macos]` / `[windows]`** mark a criterion that holds on **one** platform
  only — and they are a claim that needs earning. Not a difference of implementation: a difference of
  *behaviour*, in something the user can see, that the other platforms genuinely cannot have. A
  capability one platform has and another does not is never dropped in silence: it carries a
  hazard-mapped sibling on the other platform (below). If a `[macos]` and a `[windows]` criterion say
  the same thing in different words, they were one `[desktop]` criterion all along; likewise an
  `[android]` and an `[ios]` that agree are `[mobile]`.
- **`[device]`** marks a criterion only observable on real hardware (background playback,
  lock-screen behaviour, audible output). It is orthogonal to the platform tags — it combines with
  them — and maps to [device-checklist.md](device-checklist.md) instead of a unit test.

## Platform differences are behaviour, not omissions

Some things one platform can do and another cannot. A Mac cannot keep monitoring through a closed
lid; a PC may not even own an H.265 decoder; a phone cannot show a picture-in-picture window over
your other work.

**A criterion is never silently dropped for a platform.** It is shared, or it is platform-tagged
with an equivalent that addresses the same *hazard*, or it is explicitly marked unsupported **with
a stated consequence** — what the app does, and what it tells the user, in place of it.

Map hazard to hazard, not feature to feature. "Android warns when it is not exempt from battery
optimisation" (BG-9) looks Android-only, but the hazard it guards — *the OS quietly suspends the
monitor overnight* — exists on a Mac, on a PC and on an iPhone too, wearing different clothes. So the
desktops have their own criterion for the same hazard (BG-12), and iOS one for how it stays alive and
what it says when it cannot (BG-9i) — never a gap.

This follows directly from the first principle: a capability the user could mistake for a working
one is a bug, not a gap. Silence must never be mistaken for a calm baby.

## Flow

- **APP-1** Opening the app routes by stored state: no session → sign-in; session but no selected
  camera → camera selection; session and selected camera → live feed. No other entry screens.
- **APP-2** The app never asks again for anything it already knows: credentials while the session
  is refreshable, or camera choice while one is selected.
- **APP-3** Every failure surfaced to the user (sign-in, device list, stream) is a readable
  message with a way to retry — never a crash or a dead end.

## Presentation

- **UI-1** `[device]` The app renders in a dark theme suitable for use in a dark room at night.
- **UI-2** `[device]` All user-facing text is in English.
- **UI-3** `[device]` The app has **one icon**, and it is the same mark on every platform it ships
  on — wherever the OS shows an app: the launcher, recents and settings on a phone; the Dock,
  Mission Control and the switcher on a Mac; the taskbar, Alt-Tab and Explorer on a PC. It is never
  a generic placeholder, and a new platform takes the same mark rather than inventing one. One
  monitor, one face.
