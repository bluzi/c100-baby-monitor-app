# Background monitoring

The defining feature: monitoring is not tied to the visible app. Once the live feed has started,
audio (when unmuted) and the noise alarm keep running with the app unfocused, the screen locked,
and the screen off.

- **BG-1** `[device]` With monitoring on, live audio keeps playing when the user leaves the app,
  locks the screen, or the screen turns off.
- **BG-2** `[device]` While monitoring, a persistent notification shows which camera is being
  monitored and the current feed state (live / reconnecting / error), kept current as the state
  changes; tapping it returns to the live feed.
- **BG-3** `[device]` The notification offers a Stop action that ends monitoring (audio, alarm,
  connection) even without opening the app.
- **BG-4** `[device]` The noise alarm keeps working in the background and while muted (see
  ALRM-4/LIVE-3).
- **BG-5** `[device]` Monitoring runs until the user stops it (notification Stop, or the live
  feed's stop control — BG-11) or signs out — navigating
  away, locking the phone, or the app UI being killed never stops it. Reopening the app while
  monitoring shows the ongoing feed without restarting the stream; reopening after Stop starts
  monitoring again.
- **BG-6** `[device]` Auto-reconnect (LIVE-5) also runs in the background: a Wi-Fi blip at night
  recovers by itself without unlocking the phone.
- **BG-7** `[device]` The app shows over the lock screen: opening it on a locked phone shows the
  live feed without unlocking, so a parent can glance at the baby at night. This deliberately
  trades device-lock protection for convenience — the whole UI (settings, sign-out) is reachable
  there.
- **BG-8** If the stored session expires while monitoring and cannot be refreshed, the app does
  not retry forever in silence: it says so (in the app and in the monitoring notification), the
  feed watchdog treats the feed as down, and opening the app leads back to sign-in (AUTH-8).
- **BG-9** `[device]` Because the phone's battery optimisation can suspend an overnight monitor,
  the live feed warns while the app is not exempt from it and offers to request the exemption.
- **BG-10** `[device]` If the phone restarts while monitoring (an overnight OS update), the app
  says so with a notification that resumes monitoring when tapped — a restart never leaves the
  parent believing the monitor is still running.
- **BG-11** While monitoring runs, the live feed offers a stop control that ends monitoring
  (audio, alarm, connection) exactly like the notification's Stop; while monitoring is stopped,
  it offers start instead — never both stop and nothing, never a dead end. A single stray tap
  can never stop monitoring: stopping asks to be confirmed `[device]`. The stopped feed says
  monitoring is stopped and starting again is one tap away.
