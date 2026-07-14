# Windows shell

`[windows]` throughout. The PC's own surface: a notification-area (tray) icon, one monitor window
that takes either of two shapes — a full window, or a small tile that floats over other work — and
the things Windows does differently from every other platform this app runs on.

The monitor itself is the shared one — connection, reconnect, watchdog, cry detection and alarm all
behave exactly as the [app spec](../app.spec.md) says. This file is only about the shell around it,
and about the two places where a PC can do **less** than a phone (sleep, and H.265) and must say so.

It is deliberately the Mac's shell, in Windows's language: the same window, the same two shapes, the
same rule about what may fade. Where a criterion here mirrors a `MACOS-*` one, it is because the
hazard is the same and the answer is the same — the *nouns* differ (a tray icon, not a menu bar
item), never the promise.

## The tray icon is the app

- **WIN-1** `[device]` The app lives in the notification area. Its icon shows the feed state at a
  glance — monitoring and live, reconnecting, stopped, or in error — and stays current as the state
  changes. A ringing, unacknowledged alarm is unmistakable in the icon (ALRM-4).
- **WIN-2** The tray menu names the camera being monitored and its current state in words, and
  offers, at minimum: mute, show the monitor, switch it between its two shapes (WIN-5),
  acknowledge (only while an alarm rings), stop/start monitoring, settings, and exit. It is a real
  Windows context menu, opened with either mouse button, and it dismisses the way every other one
  on the desktop does.
- **WIN-3** `[device]` Stop and start in the menu do exactly what the live feed's controls do
  (BG-11), and Stop asks to be confirmed — a stray click in a menu can no more end monitoring than
  a stray tap can.
- **WIN-4** `[device]` Mute in the menu is the same mute as everywhere else (LIVE-2): it silences
  the speaker only. **Level monitoring and the crying alarm keep working while muted** (LIVE-3), and
  the menu says which state it is in (a checked item) rather than what clicking would do.
- **WIN-9** Exit is the only thing that ends the app. Closing a window never does (BG-5) — it hides
  to the tray, and says so the first time — and the app never exits itself: not on an update
  (UPD-5), not on an error.

## One window, two shapes

The monitor is **one window**. It is either *full* — a normal window you work in — or *mini* — a
small tile that stays on top of everything else. These are two shapes of the same thing, never two
windows: at most one of them is ever on screen.

- **WIN-6** `[device]` In its **full** shape the window shows the camera's video edge to edge with
  the status, the level indicator and the controls overlaid on it — the same controls, in the same
  states, as the phone's and the Mac's (BG-11, LIVE-2, LIVE-4, LIVE-6, LIVE-10). It can go full
  screen (F11).
- **WIN-5** `[device]` In its **mini** shape it is small and always on top, sitting where the user
  left it. It floats over other applications and over maximised windows, so the baby is visible
  while working (BG-7w). It shows the video, the feed state, and — while the pointer is over it —
  mute, acknowledge, and the control that makes it full again. It can be moved and resized, and it
  keeps the video's shape while resizing.
- **WIN-14** Switching shape is one action, available from the window, from the tray menu and from
  the keyboard, and it goes both ways. **It never restarts the stream**: the picture does not black
  out, no reconnect happens, and audio is not interrupted — because it is the same window and the
  same feed. Each shape remembers its own size and position, separately, across relaunches.
- **WIN-15** `[device]` The mini shape says what it will do before it is clicked: with the pointer
  over it, its controls and an explicit "make it full" control appear; with the pointer away, they
  are gone and only the picture and the feed state remain.
- **WIN-16** `[device]` **The mini shape fades out of the way.** While the pointer is elsewhere it
  is translucent, so the work underneath stays readable; the moment the pointer is over it, it is
  fully opaque again. How faint it goes is a setting, and fading can be turned off entirely; it is
  also off whenever the system's **transparency effects** are off.
  **It never fades while anything needs attention** — an alarm ringing, a feed that is not live
  while monitoring, an expired session, or a sleep outage still to be read. A warning nobody can
  read is not a warning.
- **WIN-19** `[device]` **The window takes the camera's shape.** Once the feed has a picture, both
  shapes keep the video's own aspect ratio, so the picture fills the window and is never framed in
  black bars; resizing keeps that shape. The exception is full screen, where the shape is the
  screen's — there the unused area is black, as it is in every other video app on Windows.
- **WIN-7** `[device]` Closing the window closes the window and nothing else: monitoring, audio and
  the alarm carry on, and the tray icon remains (BG-5). Reopening it shows the ongoing feed without
  restarting the stream, in the shape it was last in.
- **WIN-12** `[device]` While the window is open, the app can be reached the way every other app
  can — the taskbar and Alt-Tab find it. With it closed the app recedes into the tray and stops
  cluttering the switcher. A window a parent has to go hunting for is a window they will not check.

## A Windows app behaves like a Windows app

- **WIN-13** `[device]` The app uses the platform's own conventions rather than a translation of
  another one's: **Ctrl-V pastes into every field the app asks a user to type into** (a Mi password
  that cannot be pasted is a password typed wrong, at 3am, in the dark), Ctrl-C/Ctrl-X/Ctrl-A work
  alongside it, Esc leaves full screen, F11 enters it, Alt-F4 closes the window (and only the
  window — WIN-9), and Enter submits the form that is on screen.
- **WIN-17** `[device]` The app has an icon of its own — **the same mark as the phone's and the
  Mac's** (UI-3) — wherever Windows shows one: the taskbar, Alt-Tab, the tray, the window's title
  bar, and Explorer.
- **WIN-18** `[device]` The app respects the system's own settings: with **transparency effects**
  off it draws solid surfaces instead of translucent ones (and the mini shape does not fade —
  WIN-16); with **animation effects** off it changes shape and reveals controls without animating;
  it follows the system accent colour where it uses one.

## Starting up

- **WIN-8** `[device]` The app offers to start with Windows, so a PC that restarts overnight comes
  back to a running monitor (BG-13w). The offer is made once, plainly, where the parent will see
  it, and it can be declined; it can be turned on or off later in settings. **The app never turns it
  on by itself**, and it says which state it is in rather than what clicking would do.
- **WIN-10** `[device]` The app keeps the display awake while the feed is live and a window is
  showing it (LIVE-14), and keeps the system awake for as long as monitoring runs (BG-12w). The
  display is let go the moment the window is closed or the feed stops being live — a monitor nobody
  is looking at must not hold a screen on all night.

## What a PC cannot do, said out loud

- **AUTH-6w** `[windows]` `[device]` The Mi account token is stored encrypted (AUTH-6), tied to this
  Windows user account on this machine, and the app reads it back **without ever asking the user for
  anything** — including after an update, when the binary has changed. If the store refuses or the
  item is gone, the session is dropped and the app asks for a sign-in. It never crashes, and it
  never falls back to storing the token unencrypted.

- **WIN-11** `[device]` A PC that is asleep runs nothing, and no application can prevent the sleep a
  user *asks* for (the lid, the power button, Start → Sleep). The app therefore:
  1. says so plainly where a parent would rely on it — before an overnight watch, not after;
  2. detects that the machine slept, and on wake reports the outage and how long it lasted, rather
     than reconnecting quietly as though nothing had happened;
  3. treats the gap as a real outage: the watchdog does not pretend the feed was alive (WATCH-2).

- **WIN-20** `[device]` **Windows does not always ship an H.265 decoder.** The camera sends H.265
  and nothing else, so on a PC without the codec there is no picture — and a black rectangle that
  never explains itself is exactly the kind of silence this app refuses. When the system cannot
  decode the video, the app says so in plain words on the feed, points at the free **HEVC Video
  Extensions** in the Microsoft Store, and **keeps monitoring**: audio, the level meter, the crying
  alarm and the watchdog are all unaffected (LIVE-7). Sound is what monitoring means; the picture is
  a convenience.

- **WIN-21** `[device]` A PC has no vibration motor and no separate alarm-volume channel, so the
  phone's answers to "an alarm nobody can hear" (ALRM-10) do not exist here. Instead: the alarm
  plays at its configured volume (ALRM-11/14) on the system's default output, on its own audio path
  that a muted feed cannot silence, and the tray icon makes a ringing alarm unmistakable (WIN-1)
  so a PC with its speakers off still **shows** the alarm. The vibrate setting is not offered rather
  than being offered and ignored.
