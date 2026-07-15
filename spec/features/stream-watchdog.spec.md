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
  reconnecting / error) as it changes — the notification on Android (BG-2), the Live Activity on iOS
  (BG-2i), the status icon on a desktop (BG-15).
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
