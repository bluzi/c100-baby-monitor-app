# Background monitoring

The defining feature: monitoring is not tied to the visible app. Once the live feed has started,
audio (when unmuted) and the noise alarm keep running with the app unfocused, the window closed,
the screen locked, and the screen off.

The shape of "the app is not visible" differs by platform, and so does the way each OS threatens
to suspend a monitor overnight. The *guarantee* does not. Where a criterion is tagged, look for
its sibling: they are two answers to one hazard, not one platform getting less. A Mac and a PC
answer as one — `[desktop]` — because a desktop is a desktop.

## The guarantee

- **BG-1** `[device]` With monitoring on, live audio keeps playing when the user leaves the app,
  closes its window, locks the screen, or the screen turns off.
- **BG-4** `[device]` The noise alarm keeps working in the background and while muted (see
  ALRM-4/LIVE-3).
- **BG-5** `[device]` Monitoring runs until the user stops it (BG-3, or the live feed's stop
  control — BG-11) or signs out — navigating away, locking the device, closing the window, or the
  app's UI being killed never stops it. Reopening the UI while monitoring shows the ongoing feed
  without restarting the stream; reopening after Stop starts monitoring again.
- **BG-6** `[device]` Auto-reconnect (LIVE-5) also runs in the background: a Wi-Fi blip at night
  recovers by itself without touching the device.
- **BG-8** If the stored session expires while monitoring and cannot be refreshed, the app does
  not retry forever in silence: it says so (in the app and wherever it reports state — the
  notification or the menu bar), the feed watchdog treats the feed as down, and opening the app
  leads back to sign-in (AUTH-8).
- **BG-11** `[android]` While monitoring runs, the live feed offers a stop control that ends
  monitoring (audio, alarm, connection) exactly like BG-3's; while monitoring is stopped, it offers
  start instead — never both stop and nothing, never a dead end. A single stray tap can never stop
  monitoring: stopping asks to be confirmed `[device]`. The stopped feed says monitoring is
  stopped and starting again is one tap away.
- **BG-14** `[desktop]` **A desktop has no stop control, because there the app *is* the monitor.** It
  starts watching when it opens and watches until it is quit (Quit on a Mac, Exit on a PC). There is
  therefore no such thing as Baby Monitor running and not monitoring — which is the quietest failure
  this app could have, an app sitting on screen or in the tray looking alive over a watch that ended
  hours ago. Quitting is how a desktop stops (DESK-6), and **quitting asks to be confirmed while
  monitoring is running** `[device]`: BG-11's protection against a single stray click, moved onto the
  control that now carries its weight.
  A monitor that failed on its own (WATCH-11) still offers Start, because *that* must be recoverable
  without quitting the app.
  The phone keeps its stop control: a phone app that must be force-quit to stop watching is not the
  same bargain, and the notification (BG-3) is where a parent expects to find it.

## Where the state is reported, and where it is stopped from

- **BG-2** `[android]` `[device]` While monitoring, a persistent notification shows which camera
  is being monitored and the current feed state (live / reconnecting / error), kept current as the
  state changes; tapping it returns to the live feed.
- **BG-3** `[android]` `[device]` The notification offers a Stop action that ends monitoring
  (audio, alarm, connection) even without opening the app.
- **BG-15** `[desktop]` `[device]` While monitoring, the status icon — the menu bar item on a Mac,
  the tray icon on a PC — says at a glance whether anything is wrong, and its menu names the camera
  being monitored and its state (live / reconnecting / error) in words. Opening the menu never
  interrupts monitoring. (See DESK-1.)
- **BG-16** `[desktop]` `[device]` The status menu offers Quit — which is how a desktop stops
  monitoring (BG-14) — without opening any window, and asks to be confirmed while the monitor is
  running.

## Glancing at the baby without ceremony

- **BG-7** `[android]` `[device]` The app shows over the lock screen: opening it on a locked phone
  shows the live feed without unlocking, so a parent can glance at the baby at night. This
  deliberately trades device-lock protection for convenience — the whole UI (settings, sign-out)
  is reachable there.
- **BG-17** `[desktop]` `[device]` The same intent, and a desktop cannot answer it the same way: an
  app cannot draw over the macOS or Windows lock screen, and this app does not pretend to. Instead
  the glance is always available *while the machine is unlocked* — the always-on-top mini window
  (DESK-8) floats over other work, over full-screen and maximised windows, and on a Mac across
  spaces, so checking the baby never means finding and raising a window. A locked machine must be
  unlocked first; the app never suggests otherwise.

## The OS suspending the monitor overnight

- **BG-9** `[android]` `[device]` Because the phone's battery optimisation can suspend an
  overnight monitor, the live feed warns while the app is not exempt from it and offers to request
  the exemption.
- **BG-12** `[desktop]` `[device]` Because a sleeping machine runs nothing at all, the app asks the
  system to stay awake for as long as monitoring is running, so an idle Mac or PC does not suspend
  the monitor. If that request is refused, the app says so rather than appearing to monitor.
  **It cannot prevent sleep the user asks for** — the lid, the power button, Apple menu → Sleep,
  Start → Sleep — and nothing an app can do will change that. What the app does about *that* is
  DESK-21: it says so before a parent relies on it overnight, and on wake it reports that monitoring
  was down and for how long. It never resumes quietly, as though the night had been covered.
- **BG-10** `[android]` `[device]` If the phone restarts while monitoring (an overnight OS
  update), the app says so with a notification that resumes monitoring when tapped — a restart
  never leaves the parent believing the monitor is still running.
- **BG-13** `[desktop]` `[device]` If the machine restarts, or the app is quit, while monitoring,
  the app says so the next time it starts and resumes monitoring in one click. To make that rare, it
  offers to start at login (DESK-19). A restart never leaves the parent believing the monitor is
  still running.
