# Desktop shell

`[desktop]` throughout — **macOS and Windows**. A desktop has things a phone does not: a status icon
that lives among the system's own, a window that can float over other work, a keyboard, and a
machine that sleeps. This is the shell around the monitor on both of them.

It is deliberately **one shell in two languages**. The nouns differ — a menu bar item on a Mac, a
tray icon on a PC; Quit there, Exit here — and where they do, both are named. The promise never
differs. A criterion is tagged `[macos]` or `[windows]` only where the *behaviour* genuinely holds
on one and not the other, which below happens exactly once (DESK-22: a Mac always has an H.265
decoder and a PC may not).

The monitor itself is the shared one — connection, reconnect, watchdog, cry detection and alarm all
behave as the [app spec](../app.spec.md) says. This file is only about the shell, and about the two
places where a desktop can do **less** than a phone (sleep, and the lock screen) and must say so.

## The status icon is the app

- **DESK-1** `[device]` The app lives in the status area — the **menu bar** on a Mac, the
  **notification area** on a PC — and **while the monitor is doing its job the icon is the app's own
  mark** (UI-3): a plain waveform, drawn the way that platform draws its status icons (on a Mac a
  template, so it is white on a dark bar and black on a light one, and never a blob of the wrong
  colour). It changes only when something is wrong, and then it changes unmistakably: a **ringing,
  unacknowledged alarm** (ALRM-4) and a **monitor that is not watching** — an expired session, an
  unsupported camera, a monitor that failed — each get their own icon, in their own colour. A status
  area that looks the same whether the monitor is live or dead is the failure this whole project is
  built against, so the fault states are loud and the working state is quiet. (Connecting and
  reconnecting are the monitor working, not failing, and stay quiet.)
- **DESK-2** `[device]` **The status menu offers what the app can actually do right now, and nothing else.**
  There are three situations and they are genuinely different, so the menu is:
  - **Not signed in** — it says so, and offers to sign in. Mute, Show Camera and the rest are not
    dimmed, they are *absent*: a menu describing a monitor that does not exist is a menu that lies.
  - **Signed in, no camera chosen** — it says so, and offers to choose one, or to sign out.
  - **Watching** — it names the camera and its state in words, and offers, at minimum: mute, show the
    monitor, switch it between its two shapes (DESK-8), acknowledge (only while an alarm rings),
    start (only when the monitor has stopped working — BG-14), **the cameras on the account as a
    submenu with the watched one checked** (CAM-4), settings, and sign out.

  Every one of them offers to check for updates (UPD-9), and to quit (BG-14).
  Picking another camera from the submenu switches to it there and then — a parent with two children
  must not have to walk back through the picker to look at the other room. The list is fetched when
  the menu is opened, never on the state tick: it is a signed request to Xiaomi, and an account that
  rate-limits itself is an account that cannot reconnect.

  It is the platform's own status menu, opened the way that platform opens one (on a PC, with either
  mouse button), and it dismisses the way every other one on that desktop does.
- **DESK-3** `[device]` **Quitting is how a desktop stops monitoring** (BG-14), and it asks to be
  confirmed while the monitor is running — a stray click in a menu can no more end a watch than a
  stray tap can. There is no Stop control anywhere: a desktop does not have one.
- **DESK-4** `[device]` Mute in the status menu is the same mute as everywhere else (LIVE-2): it
  silences the speaker only. **Level monitoring and the crying alarm keep working while muted**
  (LIVE-3), and the menu says which state it is in rather than what clicking would do.
- **DESK-5** `[device]` **Sign-in and the camera picker are dialogs, not the monitor.** They have no
  video to float and they have fields to type into, so they are never worn as a tile — the window is
  full whatever shape it was last in (DESK-9) — and on a Mac they take the shape of the system's own
  authorisation panel: borderless, with no traffic lights.
  Which is exactly why they **carry their own way out**, on the screen itself. Closing the window does
  not end the app — that is right for a monitor (DESK-13) and wrong for a parent who has decided not
  to use the app at all, and a panel with no close button would leave them nothing to press. The way
  out must be where they are looking, not behind a status icon they have not learned yet. Nothing is
  being monitored on either screen, so quitting there is only quitting: it ends no watch, and asks
  nothing (BG-14).
- **DESK-6** `[device]` Quitting is the only thing that ends the app — and, because the app watches until it is
  quit (BG-14), it is the only thing that ends the watch. Closing a window never does (BG-5). The app
  never quits or restarts itself: not on an update (UPD-5), not on an error.

## One window, two shapes

The monitor is **one window**. It is either *full* — a normal window you work in — or *mini* — a
small tile that floats over everything else. These are two shapes of the same thing, never two
windows: at most one of them is ever on screen, and the parent never has to wonder which one they
are looking at, or close one to be rid of the other.

- **DESK-7** `[device]` In its **full** shape the window shows the camera's video edge to edge with
  the status, the level indicator and the controls overlaid on it (LIVE-2, LIVE-4, LIVE-6, LIVE-10)
  — minus the phone's stop control, which a desktop does not have (BG-14) — and the rare actions
  behind a menu: switch camera, sign out, check for updates (UPD-9), and quit. It can go full screen.
- **DESK-8** `[device]` In its **mini** shape it is small and always on top, sitting where the user
  left it. It floats over other applications — over full-screen and maximised windows, and on a Mac
  across spaces — so the baby is visible while working (BG-17). It shows the video, the feed state,
  and two controls that are **always there, not only on hover**:
  - **mute**, because a tile is too small to spend words on "muted", so the mute control itself
    carries it, latched and unmistakable (LIVE-2);
  - **acknowledge**, whenever an alarm is ringing — an alarm you must first go hunting for the
    controls to silence is an alarm that rings longer than it should, and LIVE-17's rule holds here
    too: what needs attention never hides.

  Close, and the control that makes it full again, appear with the pointer (DESK-10). It can be moved
  and resized, and it keeps the video's shape while resizing.
- **DESK-9** Switching shape is one action, available from the window, from the status menu and from
  the keyboard, and it goes both ways. **It never restarts the stream**: the picture does not black
  out, no reconnect happens, and audio is not interrupted — because it is the same window and the
  same feed. Each shape remembers its own size and position, separately, across relaunches.
- **DESK-10** `[device]` The mini shape says what it will do before it is clicked: with the pointer
  over it, close and an explicit "make it full" control appear; with the pointer away, they are gone
  and the picture, the feed state and the always-on controls (DESK-8) remain. It is never a mystery
  tile that does something surprising when clicked. Crossing the pointer over one of its own controls
  is not leaving it — the tile does not flicker, and never fades out from under the pointer.
- **DESK-11** `[device]` **The mini shape fades out of the way.** While the pointer is elsewhere it
  is translucent, so the work underneath stays readable; the moment the pointer is over it, it is
  fully opaque again. How faint it goes is a setting, and fading can be turned off entirely; it is
  also off whenever the system's own transparency setting is (Reduce Transparency on a Mac,
  transparency effects on a PC).
  **It never fades while anything needs attention** — an alarm ringing, a feed that is not live while
  monitoring, an expired session, or a sleep outage still to be read. A warning nobody can read is
  not a warning, and this app's first promise is that silence is never mistaken for a calm baby.
- **DESK-12** `[device]` **The window takes the camera's shape.** Once the feed has a picture, both
  shapes keep the video's own aspect ratio, so the picture fills the window edge to edge and is never
  framed in black bars; resizing keeps that shape. The exception is full screen, where the shape is
  the screen's and cannot be changed — there the unused area is black, as it is in every other video
  app on the platform.
- **DESK-13** `[device]` Closing the window closes the window and nothing else: monitoring, audio
  and the alarm carry on, and the status icon remains (BG-5). Reopening it shows the ongoing feed
  without restarting the stream, in the shape it was last in.
- **DESK-14** `[device]` **While a window is open, the app can be reached the way every other app
  can** — the switcher finds it, and on a Mac it has a Dock icon. With every window closed the app
  recedes into the status area and stops cluttering the switcher: a monitor watching quietly in the
  background is not an application anyone is trying to switch to. A window a parent has to go hunting
  for is a window they will not check.
- **DESK-15** `[device]` **The mini tile is never listed among the windows.** It is already on top of
  everything; listing it among the windows a user is trying to see *past* makes it clutter twice
  over. So it stays out of Mission Control and the window cycles on a Mac, and out of Alt-Tab and the
  taskbar on a PC.
  The two desktops draw the line in different places because they switch different things: a Mac
  switches *apps*, so with only the tile up the app is still in Cmd-Tab and still in the Dock (which
  is right — it is how you reach it); Windows switches *windows*, so the tile itself is excluded and
  the full window is what Alt-Tab shows. Either way the promise is the same: the tile is reachable,
  and it is never in the way.

## A desktop app behaves like the desktop it is on

- **DESK-16** `[device]` The app uses the platform's own conventions rather than a translation of
  another platform's. **A paste works in every field the app asks a user to type into** — Cmd-V on a
  Mac, Ctrl-V on a PC — with cut, copy and select-all beside it under their usual keys. A password
  manager must work: a Mi password that cannot be pasted is a password typed wrong, at 3am, in the
  dark. (On a Mac that means the standard menu bar — App with About/Settings (Cmd-,)/Hide/Quit, plus
  Edit, View and Window. A menu bar app with no menu bar has no Edit menu, and Cmd-V silently does
  nothing: this was a real bug.) The rest of the platform's keys work as that platform's users
  expect: entering and leaving full screen, closing the window (and only the window — DESK-6), and
  submitting the form that is on screen.
- **DESK-17** `[device]` The app has an icon of its own — **the same mark as the phone's** (UI-3) —
  wherever the OS shows one: the switcher, the Dock or the taskbar, the status area, the window, and
  the file manager.
- **DESK-18** `[device]` The app respects the system's own display settings: with transparency
  reduced or off it draws solid surfaces instead of translucent ones (and the mini shape does not
  fade — DESK-11); with motion reduced or animations off it changes shape and reveals controls
  without animating; it follows the system accent colour where it uses one.

## Starting up

- **DESK-19** `[device]` The app offers to start when the user logs in, so a machine that restarts
  overnight comes back to a running monitor (BG-13). The offer is made once, plainly, where the
  parent will see it, and it can be declined; it can be turned on or off later in settings. **The app
  never turns it on by itself**, and it says which state it is in rather than what clicking would do.
- **DESK-20** `[device]` The app keeps the display awake while the feed is live and a window is
  showing it (LIVE-14), and keeps the machine awake for as long as monitoring runs (BG-12). The
  display is let go the moment the window is closed or the feed stops being live — a monitor nobody
  is looking at must not hold a screen on all night.

## What a desktop cannot do, said out loud

- **DESK-21** `[device]` A sleeping machine runs nothing, and **no application can prevent the sleep
  a user asks for** — closing a lid, the power button, Apple menu → Sleep, Start → Sleep. The app
  therefore:
  1. says so plainly where a parent would rely on it — before an overnight watch, not after;
  2. detects that the machine slept, and on wake reports the outage **and how long it lasted**,
     rather than reconnecting quietly as though nothing had happened;
  3. treats the gap as a real outage: the watchdog does not pretend the feed was alive (WATCH-2).

  A parent who closes the lid must not be able to believe the baby was being monitored.
- **DESK-22** `[windows]` `[device]` **Windows does not always ship an H.265 decoder** (a Mac
  always has one, which is why this is the one criterion here that is not shared). The camera sends
  H.265 and nothing else, so on a PC without the codec there is no picture — and a black rectangle
  that never explains itself is exactly the kind of silence this app refuses. When the system cannot
  decode the video, the app says so in plain words on the feed, points at the free **HEVC Video
  Extensions** in the Microsoft Store, and **keeps monitoring**: audio, the level meter, the crying
  alarm and the watchdog are all unaffected (LIVE-7). Sound is what monitoring means; the picture is
  a convenience.
- **DESK-23** `[device]` A desktop has no vibration motor and no separate alarm-volume channel, so
  the phone's answers to "an alarm nobody can hear" (ALRM-10) do not exist here. Instead: the alarm
  plays at its configured volume (ALRM-11, ALRM-14) on the system's default output, on its own audio
  path that a muted feed cannot silence, and the status icon makes a ringing alarm unmistakable
  (DESK-1) — so a machine with its speakers turned down still **shows** the alarm. The vibrate
  setting is not offered, rather than being offered and ignored.
