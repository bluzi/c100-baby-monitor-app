# macOS shell

`[macos]` throughout. The Mac's own surface: a menu bar item, a main window with the camera, and a
small always-on-top window that floats over other work. Everything a Mac has that a phone does not.

The monitor itself is the shared one — connection, reconnect, watchdog, cry detection and alarm all
behave exactly as the [app spec](../app.spec.md) says, because they are the same code. This file is
only about the shell around it.

## The menu bar is the app

- **MACOS-1** `[device]` The app lives in the menu bar. Its icon shows the feed state at a glance —
  monitoring and live, reconnecting, stopped, or in error — and stays current as the state changes.
  A ringing, unacknowledged alarm is unmistakable in the icon (ALRM-4).
- **MACOS-2** The menu bar menu names the camera being monitored and its current state in words,
  and offers, at minimum: mute, show the main window, show the mini window, acknowledge (only while
  an alarm rings), stop/start monitoring, settings, and quit.
- **MACOS-3** `[device]` Stop and start in the menu do exactly what the live feed's controls do
  (BG-11), and Stop asks to be confirmed — a stray click in a menu can no more end monitoring than
  a stray tap can.
- **MACOS-4** `[device]` Mute in the menu is the same mute as everywhere else (LIVE-2): it silences
  the speaker only. **Level monitoring and the crying alarm keep working while muted** (LIVE-3), and
  the menu says which state it is in rather than what clicking would do.
- **MACOS-9** Quit is the only thing that ends the app. Closing a window never does (BG-5), and the
  app never quits itself — not on an update (UPD-5), not on an error.

## Windows

- **MACOS-6** `[device]` The main window shows the camera's video full-bleed with the status, the
  level indicator and the controls overlaid on it — the same controls, in the same states, as the
  phone's (BG-11, LIVE-2, LIVE-4, LIVE-6, LIVE-10). It can go full screen.
- **MACOS-7** `[device]` Closing the main window closes the window and nothing else: monitoring,
  audio and the alarm carry on, and the menu bar item remains (BG-5). Reopening it shows the ongoing
  feed without restarting the stream.
- **MACOS-5** `[device]` A mini window can be shown from the menu: small, always on top, sitting in a
  corner of the screen. It floats over other applications, over full-screen apps and across spaces,
  so the baby is visible while working. It shows the video and the feed state, it can be moved and
  resized, and its position is remembered. Closing it never stops monitoring; it is a view, not the
  monitor (BG-7m).
- **MACOS-12** `[device]` While a window is open, the app can be reached the way every other app can
  — Cmd-Tab and Mission Control find it. With no window open it recedes into the menu bar and stops
  cluttering the switcher. A window a parent has to go hunting for is a window they will not check.

## Starting up

- **MACOS-8** `[device]` The app offers to open at login, so a Mac that restarts overnight comes
  back to a running monitor (BG-13). The offer can be declined and changed later; the app never
  turns it on by itself.
- **MACOS-10** `[device]` The app keeps the display awake while the feed is live and a window is
  showing it (LIVE-14), and holds a system sleep inhibitor for as long as monitoring runs (BG-12).

## What a Mac cannot do, said out loud

- **MACOS-11** `[device]` A Mac cannot monitor through a closed lid, and no inhibitor changes that
  (BG-12). The app therefore:
  1. says so plainly where a parent would rely on it — before an overnight watch, not after;
  2. detects that the Mac slept, and on wake reports the outage and how long it lasted, rather
     than reconnecting quietly as though nothing had happened;
  3. treats the gap as a real outage: the watchdog does not pretend the feed was alive (WATCH-2).
  A parent who closes the lid must not be able to believe the baby was being monitored.
