# iOS shell

`[ios]` throughout. The iPhone's own surface: a normal iOS app with a full-bleed live feed, a Live
Activity that carries the monitor onto the lock screen and the Dynamic Island, and the honesty about
what an iPhone can do that a Mac cannot — and what it cannot do that an Android phone can.

The monitor itself is the shared one — connection, reconnect, watchdog, cry detection and alarm all
behave exactly as the [app spec](../app.spec.md) says, because they are the same code. This file is
only about the shell around it, and the places where iOS's rules differ from the other shells'.

## A phone app that behaves like a phone app

- **IOS-1** `[device]` The app has an icon of its own wherever iOS shows one — the Home Screen, the
  App Library, the app switcher, Settings — and it is the same mark the other platforms show (UI-3),
  never a placeholder. Every screen renders dark (UI-1), and the live feed is landscape (LIVE-9); the
  sign-in, camera picker and settings are ordinary portrait screens.
- **IOS-2** `[device]` Every field the app asks a parent to type into accepts a paste — a Mi password
  from a password manager must go in without being retyped, at 3am, in the dark (the hazard DESK-16
  guards on a desktop; iOS gives it for free through standard text fields). Sign-in opens with the
  keyboard already up on the first field, and the two-factor step on its code field (AUTH-11).

## The status lives in a Live Activity

- **IOS-3** `[device]` While the monitor is doing its job its status is a **Live Activity** on the
  lock screen and in the **Dynamic Island** (BG-2i): the watched camera and the feed state (live /
  reconnecting / error), kept current, with a **Stop** control (BG-3i). Like a desktop's status icon
  (DESK-1) it is quiet while all is well and loud when something is wrong — **a ringing alarm and a
  monitor that has stopped working are unmistakable** on the island and the lock screen, so a glance
  tells the truth even from across a dark room. Tapping it opens the live feed. Because the app is
  playing audio it also appears in Control Center's Now Playing, named for the camera.

## Staying alive in the background

- **IOS-4** `[device]` Monitoring survives the app being unfocused, the phone locked and the screen
  off, because the app keeps audio alive in the background (BG-9i) — that is the whole mechanism, and
  it is why a muted feed still feeds a silent speaker (LIVE-3) rather than pausing. A phone call or
  Siri interrupts the audio; when the interruption ends the monitor resumes on its own. If the audio
  session cannot be reclaimed — another app holds it, an interruption that never ends — the monitor
  does not pretend to run: it reports that monitoring stopped (WATCH-11), because a monitor that has
  quietly lost its ears is the failure this project exists to prevent.
- **IOS-5** `[device]` Alarms post a local notification with an Acknowledge action (ALRM-4). The app
  asks for notification permission once; if it is refused, **the alarm still sounds and still
  vibrates** — a denied permission never silences the alarm — and the app says the notification will
  not appear, so a parent knows what they gave up.

## What an iPhone cannot do, said out loud

A capability a parent could mistake for a working one is a bug, not a gap. Where iOS is weaker than an
Android phone, it says so plainly, in the app, before a parent relies on it overnight.

- **IOS-6** `[device]` Four honest limits, each with its stated consequence:
  1. **No live feed over the lock screen** (BG-7i). iOS will not let an app draw its video on the
     lock screen; the Live Activity shows the monitor is alive, and the picture waits behind an
     unlock. The app never pretends the lock screen shows the baby.
  2. **No resume after a restart** (BG-10i). iOS cannot relaunch the app or notify after the phone
     reboots, or after the app is force-quit; the app reports that monitoring was down the next time
     it is opened, and never leaves the parent believing it kept watching.
  3. **It cannot force the volume up** (ALRM-10i). The alarm ignores the silent switch and vibrates,
     but a volume the parent turned down the app cannot turn up — and it says the alarm may be quiet
     rather than imply otherwise.
  4. **It does not update itself** (UPD-2i). Updates come from the App Store; the app never downloads
     or restarts into one — which is UPD-5's restraint, kept by construction.
