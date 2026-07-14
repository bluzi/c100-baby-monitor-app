# App spec

C100 Baby Monitor turns a Xiaomi C100 camera into a baby monitor: sign in to the Mi account once,
pick the camera once, and from then on the app shows the camera's live video and audio. Audio
monitoring — including the optional noise alarm — keeps running with the app in the background.

It runs on **Android** (a phone) and **macOS** (a Mac, from the menu bar). It is one monitor with
two shells: the protocol, the connection engine, the reconnect and watchdog logic, the cry
detection and the alarm are the same code, and the criteria below are the same behaviour.

Feature specs: [login](features/login.spec.md) ·
[camera-selection](features/camera-selection.spec.md) · [live-feed](features/live-feed.spec.md) ·
[background](features/background.spec.md) · [noise-alarm](features/noise-alarm.spec.md) ·
[stream-watchdog](features/stream-watchdog.spec.md) · [updates](features/updates.spec.md) ·
[macos-shell](features/macos-shell.spec.md) ·
[xiaomi-protocol](features/xiaomi-protocol.spec.md)

## How to read a criterion

Criteria have stable IDs (`ALRM-3`). Every one maps to at least one test that names the ID.

- **Untagged criteria are universal.** They hold on every platform, and their tests live in the
  shared core — so they run on Android *and* on macOS. "Both apps behave the same" is not a hope;
  it is executed twice.
- **`[android]` / `[macos]`** mark a criterion that genuinely differs by platform. Not a
  difference of implementation — a difference of *behaviour*, in something the user can see.
- **`[device]`** marks a criterion only observable on real hardware (background playback,
  lock-screen behaviour, audible output). These map to
  [device-checklist.md](device-checklist.md) instead of a unit test.

## Platform differences are behaviour, not omissions

Some things one platform can do and the other cannot. A Mac cannot keep monitoring through a
closed lid; a phone cannot show a picture-in-picture window over your other work.

**A criterion is never silently dropped for a platform.** It is shared, or it is platform-tagged
with an equivalent that addresses the same *hazard*, or it is explicitly marked unsupported **with
a stated consequence** — what the app does, and what it tells the user, in place of it.

Map hazard to hazard, not feature to feature. "Android warns when it is not exempt from battery
optimisation" (BG-9) looks Android-only, but the hazard it guards — *the OS quietly suspends the
monitor overnight* — exists on a Mac too, wearing different clothes. So the Mac has its own
criterion for the same hazard (BG-12), not a gap.

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
  on — wherever the OS shows an app: the Dock, Mission Control and the switcher on a Mac; the
  launcher, recents and settings on a phone. It is never a generic placeholder, and a new platform
  takes the same mark rather than inventing one. One monitor, one face.
