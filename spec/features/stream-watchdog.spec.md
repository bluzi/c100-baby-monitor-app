# Stream watchdog

An optional alarm for the opposite failure: the **feed itself dying** — Wi-Fi loss, Mi cloud
outage, camera unplugged, or an app bug — so that silence is never mistaken for a calm baby.
The watchdog exists to guard the crying alarm: a dead feed only matters when the crying alarm
is supposed to be listening.

- **WATCH-1** Settings has a feed-watchdog toggle, **off** by default, with a configurable grace
  period in seconds (default 30).
- **WATCH-2** While monitoring is on (not stopped by the user) and the watchdog is armed
  (WATCH-9), if no live **audio** has arrived for longer than the grace period — whatever the
  cause — the phone sounds the feed-drop alarm — with its own sound, volume and vibrate
  (ALRM-11), distinct from the noise alarm by default — and posts a notification. Audio is what
  monitoring means: a feed delivering video but no usable audio is a dead feed.
- **WATCH-3** The watchdog alarm keeps sounding until acknowledged (in app or from the
  notification). Within one armed stretch, one outage fires at most one alarm: after
  acknowledgment it stays quiet until the feed has recovered and fails again. A new armed
  window is a new obligation and wins over this rule (WATCH-9).
- **WATCH-4** `[device]` The ongoing status surface always reflects the real feed state (live /
  reconnecting / error) as it changes — the notification on Android and the Live Activity on iOS
  (BG-2), the status icon on a desktop (BG-15).
- **WATCH-5** Watchdog settings persist across restarts.
- **WATCH-6** An alarm that cannot sound because another alarm is already sounding is not lost:
  it rings once the sounding alarm is acknowledged, as long as its condition still holds. A noise
  alarm ringing when the feed dies therefore never swallows the feed-drop alarm.
- **WATCH-7** A connection that stops delivering audio without dropping (camera unplugged, router
  black-holing traffic, audio stream dead while video flows) is treated as a drop within seconds:
  it is never reported as live, and it reconnects on its own like any other drop (LIVE-5).
  Silence is never mistaken for a live feed.
- **WATCH-8** No connection attempt can hang forever. A camera that never answers — switched off,
  or moved to a different address by the router — fails the attempt within seconds so the next
  attempt can happen, which is what lets the app find the camera again at its new address (LIVE-5).
- **WATCH-9** The watchdog is **armed** only while the crying alarm could itself ring: its own
  toggle is on (WATCH-1), the crying-alarm toggle is on (ALRM-1), and the time is inside the
  crying alarm's active hours (ALRM-7). While unarmed, a dead feed sounds no alarm. If the feed
  is still dead once the watchdog becomes armed (the crying alarm turned on, or its window
  opening) and it has been dead past the grace period, the alarm sounds then — armed hours must
  never begin with a dead feed and silence. This holds even for an outage already alarmed and
  acknowledged in an earlier armed window (it wins over WATCH-3). Being unarmed hides nothing
  else: the status and monitoring notification reflect the real feed state throughout (WATCH-4).
- **WATCH-10** `[device]` Settings makes the dependency visible: while the crying alarm is off,
  the watchdog controls read as inactive, and the dependency is stated next to them.
- **WATCH-11** If monitoring itself fails — an unexpected error stops the connection machinery —
  that failure is announced: the status and notification say monitoring stopped working, and are
  never dressed up as "retrying". The monitor may fail; it may never fail silently.
- **WATCH-12** **A frozen picture is never left standing as a live one.** WATCH-7 guards the
  opposite failure — audio dying while video flows — and this is its mirror: audio keeps arriving,
  so the feed is genuinely live and correctly says so, while the *picture* has stopped moving. A
  still image of a quiet cot is the most convincing lie this app can tell: it looks exactly like a
  sleeping baby, and a parent glancing at it is reassured by a photograph.

  So while the feed is live, the picture must keep **changing**, not merely keep arriving — a camera
  that repeats one frame, a stream whose timeline has stopped, and a stream that has stopped
  altogether are one failure to a parent, and all three are caught the same way. If nothing about
  the picture has changed for more than a few seconds (about 10 — the picture carries a clock, so a
  second of a working feed cannot look like this), the app **says so and reconnects it** on its own,
  exactly as it would any other drop (LIVE-5). It is never left showing the frozen picture in
  silence.

  Two things this deliberately does not do. It does not fire the watchdog alarm: the feed is live,
  audio is arriving, and the crying alarm is listening — this is a picture fault, not the dead feed
  WATCH-2 exists for, and the reconnect is over long inside the grace period anyway. And **a camera
  that never sends a picture at all is not frozen** — that is a capability gap, already said out loud
  and already leaving audio alone (LIVE-7, DESK-22); a monitor must not reconnect for ever over a
  picture it was never going to get.

  This is the one place video is allowed to interrupt audio (LIVE-7), and it is a considered
  exception: the gap is a second or two, the same as a quality change (LIVE-18), and the alternative
  is a parent trusting a photograph all night.

  **KNOWN GAP — this criterion is only half the hazard.** It is judged on what the camera *sends*, so
  it cannot see a picture that arrives, and moves, and is never *drawn*: a decoder that has wedged, or
  one suppressing every frame as faulty (DESK-26). To a parent that is the identical lie — a still cot
  and a live status — and this does not catch it. Closing it means measuring at the last possible
  moment, a frame reaching the screen, because every layer above it has already been believed once: the
  socket was busy, the frames differed, the decoder returned success, and the glass still showed a
  photograph. Until that exists, this promise stops at the wire and must not be read as covering the
  screen.
