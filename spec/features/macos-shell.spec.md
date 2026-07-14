# macOS shell

`[macos]` throughout. The Mac's own surface: a menu bar item, the standard Mac menus, and one
monitor window that takes either of two shapes — a full window, or a small tile that floats over
other work. Everything a Mac has that a phone does not.

The monitor itself is the shared one — connection, reconnect, watchdog, cry detection and alarm all
behave exactly as the [app spec](../app.spec.md) says, because they are the same code. This file is
only about the shell around it.

## The menu bar is the app

- **MACOS-1** `[device]` The app lives in the menu bar. Its icon shows the feed state at a glance —
  monitoring and live, reconnecting, stopped, or in error — and stays current as the state changes.
  A ringing, unacknowledged alarm is unmistakable in the icon (ALRM-4).
- **MACOS-2** The menu bar menu names the camera being monitored and its current state in words,
  and offers, at minimum: mute, show the monitor, switch it between its two shapes (MACOS-5),
  acknowledge (only while an alarm rings), stop/start monitoring, settings, and quit.
- **MACOS-3** `[device]` Stop and start in the menu do exactly what the live feed's controls do
  (BG-11), and Stop asks to be confirmed — a stray click in a menu can no more end monitoring than
  a stray tap can.
- **MACOS-4** `[device]` Mute in the menu is the same mute as everywhere else (LIVE-2): it silences
  the speaker only. **Level monitoring and the crying alarm keep working while muted** (LIVE-3), and
  the menu says which state it is in rather than what clicking would do.
- **MACOS-9** Quit is the only thing that ends the app. Closing a window never does (BG-5), and the
  app never quits itself — not on an update (UPD-5), not on an error.

## One window, two shapes

The monitor is **one window**. It is either *full* — a normal Mac window you work in — or *mini* — a
small tile that floats over everything else. These are two shapes of the same thing, never two
windows: at most one of them is ever on screen, and the parent never has to wonder which one they
are looking at or close one to be rid of the other.

- **MACOS-6** `[device]` In its **full** shape the window shows the camera's video full-bleed with
  the status, the level indicator and the controls overlaid on it — the same controls, in the same
  states, as the phone's (BG-11, LIVE-2, LIVE-4, LIVE-6, LIVE-10). It can go full screen.
- **MACOS-5** `[device]` In its **mini** shape it is small and always on top, sitting where the user
  left it. It floats over other applications, over full-screen apps and across spaces, so the baby
  is visible while working (BG-7m). It shows the video, the feed state, and — while the pointer is
  over it — mute, acknowledge, and the control that makes it full again. It can be moved and
  resized, and it keeps the video's shape while resizing.
- **MACOS-14** Switching shape is one action, available from the window, from the menu bar menu and
  from the keyboard, and it goes both ways. **It never restarts the stream**: the picture does not
  black out, no reconnect happens, and audio is not interrupted — because it is the same window and
  the same feed. Each shape remembers its own size and position, separately, across relaunches.
- **MACOS-15** `[device]` The mini shape says what it will do before it is clicked: with the pointer
  over it, its controls and an explicit "make it full" control appear; with the pointer away, they
  are gone and only the picture and the feed state remain. It is never a mystery tile that does
  something surprising when clicked.
- **MACOS-16** `[device]` **The mini shape fades out of the way.** While the pointer is elsewhere it
  is translucent, so the work underneath stays readable; the moment the pointer is over it, it is
  fully opaque again. How faint it goes is a setting, and fading can be turned off entirely; it is
  also off whenever the system's Reduce Transparency setting is on.
  **It never fades while anything needs attention** — an alarm ringing, a feed that is not live
  while monitoring, an expired session, or a sleep outage still to be read. A warning nobody can
  read is not a warning, and this app's first promise is that silence is never mistaken for a calm
  baby.
- **MACOS-19** `[device]` **The window takes the camera's shape.** Once the feed has a picture, both
  shapes keep the video's own aspect ratio, so the picture fills the window edge to edge and is
  never framed in black bars; resizing keeps that shape. The exception is full screen, where the
  shape is the screen's and cannot be changed — there the unused area is black, as it is in every
  other video app on the platform.
- **MACOS-7** `[device]` Closing the window closes the window and nothing else: monitoring, audio
  and the alarm carry on, and the menu bar item remains (BG-5). Reopening it shows the ongoing feed
  without restarting the stream, in the shape it was last in.
- **MACOS-12** `[device]` While the window is open, the app can be reached the way every other app
  can — Cmd-Tab and Mission Control find it. With it closed the app recedes into the menu bar and
  stops cluttering the switcher. A window a parent has to go hunting for is a window they will not
  check.

## A Mac app behaves like a Mac app

- **MACOS-13** `[device]` The app has the standard Mac menus while a window is open: About, Settings
  (Cmd-,), Hide, Quit, a Window menu, and an **Edit menu with Cut, Copy, Paste and Select All under
  their usual shortcuts**. Every field the app asks a user to type into therefore accepts a paste.
  A password manager's Cmd-V must work: a Mi password that cannot be pasted is a password typed
  wrong, at 3am, in the dark. (A menu bar app with no menu bar has no Edit menu, and Cmd-V silently
  does nothing. This was a real bug.)
- **MACOS-17** `[device]` The app has an icon of its own, wherever macOS shows one — the switcher,
  the Dock while a window is open, About, and the Finder.
- **MACOS-18** `[device]` The app respects the system's accessibility settings: with Reduce
  Transparency on it draws solid surfaces instead of translucent ones (and the mini shape does not
  fade — MACOS-16); with Reduce Motion on it changes shape and reveals controls without animating.

## Starting up

- **MACOS-8** `[device]` The app offers to open at login, so a Mac that restarts overnight comes
  back to a running monitor (BG-13). The offer is made once, plainly, where the parent will see it,
  and it can be declined; it can be turned on or off later in settings. **The app never turns it on
  by itself**, and it says which state it is in rather than what clicking would do.
- **MACOS-10** `[device]` The app keeps the display awake while the feed is live and a window is
  showing it (LIVE-14), and holds a system sleep inhibitor for as long as monitoring runs (BG-12).
  The display is let go the moment the window is closed or the feed stops being live — a monitor
  nobody is looking at must not hold a Mac's screen on all night.

## What a Mac cannot do, said out loud

- **AUTH-6m** `[macos]` `[device]` The Mi account token is stored in the Keychain (AUTH-6), and the
  app reads it back **without ever asking the user for anything** — including after an update, when
  the binary has changed. A monitor that stopped at a password box after an overnight update, with
  nobody awake to answer it, would be a monitor that failed exactly when it mattered.
  If the Keychain refuses or the item is gone, the session is dropped and the app asks for a
  sign-in. It never crashes, and it never falls back to storing the token unencrypted.

- **MACOS-11** `[device]` A Mac cannot monitor through a closed lid, and no inhibitor changes that
  (BG-12). The app therefore:
  1. says so plainly where a parent would rely on it — before an overnight watch, not after;
  2. detects that the Mac slept, and on wake reports the outage and how long it lasted, rather
     than reconnecting quietly as though nothing had happened;
  3. treats the gap as a real outage: the watchdog does not pretend the feed was alive (WATCH-2).
  A parent who closes the lid must not be able to believe the baby was being monitored.
