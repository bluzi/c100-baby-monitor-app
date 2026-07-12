# App spec

C100 Baby Monitor turns a Xiaomi C100 camera into a baby monitor on an Android phone: sign in to
the Mi account once, pick the camera once, and from then on opening the app shows the camera's
live video and audio. Audio monitoring — including the optional noise alarm — keeps running with
the app in the background and the screen locked or off.

Feature specs: [login](features/login.spec.md) ·
[camera-selection](features/camera-selection.spec.md) · [live-feed](features/live-feed.spec.md) ·
[background](features/background.spec.md) · [noise-alarm](features/noise-alarm.spec.md) ·
[stream-watchdog](features/stream-watchdog.spec.md) ·
[xiaomi-protocol](features/xiaomi-protocol.spec.md)

Criteria marked `[device]` are observable only on a real device (background/lock-screen behavior,
audible output); they map to [device-checklist.md](device-checklist.md) instead of unit tests.

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
