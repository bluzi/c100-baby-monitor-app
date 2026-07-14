# Device checklist

Manual verification for `[device]` criteria — run against a real, reachable C100 camera before
calling a release done. Each step names the criteria it verifies.

Steps 1–25 are the **Android** checklist, run on a real phone. The **macOS** checklist follows,
and covers only what differs: the monitor itself is the same code, proven by the same tests on
both platforms, so what needs a human is the shell — and the places where a Mac can do less than
a phone.

1. **Live playback (LIVE-1):** open the app (signed in, camera selected). Video renders and
   room audio is audible within a few seconds.
2. **Background + lock (BG-1, BG-4):** with audio playing, press home, then lock the phone, then
   let the screen sleep for 5+ minutes. Audio never stops. Make a sharp noise at the camera with
   the alarm enabled and the feed muted — the phone alarms.
3. **Notification (BG-2, BG-3):** while monitoring, check the persistent notification names the
   camera; tap it → live feed opens; use its Stop action → audio and connection stop and the
   notification clears.
3b. **In-app stop (BG-11):** while live, tap the live feed's stop control — a confirmation
    appears; cancel and nothing changes. Confirm — audio stops, the persistent notification
    clears, the status reads stopped, and the resume control starts monitoring again.
4. **Overnight reconnect (BG-6):** toggle the router's Wi-Fi (or the phone's) off/on while the
   screen is locked. Within a minute of Wi-Fi returning, audio resumes by itself.
5. **Video resilience (LIVE-7):** cover/uncover works trivially; stronger check — switch cameras
   during playback and confirm audio for the new camera starts even before (or without) the
   first video frame.
6. **Alarm audibility (ALRM-4):** mute the feed, set the sensitivity high, play a crying-baby clip
   at the camera for ~3 s. The phone beeps on the alarm stream and posts a notification; repeat
   immediately — no second alarm until ~30 s have passed (ALRM-5). While the Alerts dialog is
   open, the live level readout under the sensitivity slider moves with room sound and the peak
   readout tracks its maximum; closing and reopening the dialog resets the peak (ALRM-12).
7. **Keepalive stability (PROTO-19):** leave the stream running 10+ minutes — the camera must
   not drop the session (would show as a reconnect in the status line).
8. **Presentation (UI-1, UI-2, AUTH-1, CAM-1):** flip through every screen — all of them render
   dark and all copy is English. The sign-in screen asks for exactly username/email, password,
   and server region; the camera picker lists each camera with its name and model. With the
   keyboard open on the sign-in screen, the focused field and the Sign in button stay visible;
   the keyboard's action key moves username → password and submits from the password field;
   opening the server-region menu leaves the keyboard up.
8a. **Autofocus (AUTH-11):** open the sign-in screen — the username field is already focused
    with the keyboard up. Sign in so Xiaomi demands a verification code — the code field is
    focused the same way, and typing immediately fills it.
8b. **Version visible (LIVE-15):** open the live feed's top-right menu → About — the version
    shown matches the installed release's. Switch camera and Sign out are in the same menu and
    work from there (LIVE-9).
9. **Ring-until-acknowledged (ALRM-4, ALRM-5):** trigger the noise alarm — the tone repeats for
   well over 30 s until Acknowledge is tapped in the app; trigger again and acknowledge from the
   notification action instead. After acknowledging, continued noise stays quiet for ~30 s.
10. **Feed watchdog (WATCH-2, WATCH-3, WATCH-4):** enable the crying alarm and the watchdog
    (grace 10 s), kill Wi-Fi — within ~10 s a tone clearly different from the noise alarm rings
    until acknowledged, and the monitoring notification reads reconnecting/error rather than
    live. Restore Wi-Fi (it re-arms), kill it again — it fires again.
10b. **Watchdog follows the crying alarm (WATCH-9, WATCH-10):** with the watchdog on but the
    crying alarm **off**, kill Wi-Fi — no alarm rings, while the notification still reads
    reconnecting/error. In settings the watchdog controls read as inactive and say why. Turn the
    crying alarm on with the feed still dead — the watchdog alarm rings within a couple of
    seconds. Then schedule the crying alarm out of hours and kill Wi-Fi again — silent; move the
    window start to a minute from now — the alarm rings when the window opens.
11. **Landscape only + overlay (LIVE-9, LIVE-11):** open the live feed holding the phone
    upright — the display is landscape anyway; rotate the phone every which way — it stays
    landscape (flipping between the two landscape directions is fine). Video fills the screen,
    status + level on top, icon buttons at the bottom. They stay put: wait a minute and they are
    still there. Tap the **video** — they hide (system bars with them); tap it again — they come
    back. Tap the status row or a button — they never hide under your finger. Sign out — the
    sign-in screen rotates freely again.
12. **Lock screen (BG-7):** while monitoring, lock the phone and tap the monitoring
    notification (or the launcher icon) — the live feed shows without unlocking.
13. **Alarm audibility at zero volume (ALRM-10):** turn the phone's alarm volume all the way down,
    then trigger the noise alarm — it is still audible, and the volume is put back where the user
    had it after acknowledging. Repeat, but force-stop the app while it rings: on next launch the
    volume is still put back. Repeat once more and, while it rings, change the alarm volume
    yourself — after acknowledging, *your* setting is the one that survives.
13b. **Camera moved (WATCH-8, LIVE-5):** while monitoring, reboot the router so the camera comes
    back on a different IP. The app keeps retrying, picks up the new address, and returns to live
    on its own — it never sits on "Connecting…" indefinitely.
14. **Battery exemption (BG-9):** with the app not exempt from battery optimisation, the live feed
    shows the warning and its button opens the system exemption prompt; once granted, the warning
    is gone.
14b. **No-Wi-Fi warning (LIVE-13):** while on the live feed, turn Wi-Fi off (leave mobile data
    on). A warning appears saying the camera can only be reached on its own network; tapping it
    opens the system Wi-Fi panel/settings, and turning Wi-Fi back on makes the warning disappear.
14c. **Screen stays awake while live (LIVE-14):** set the screen timeout to 30 s and watch the
    live feed without touching the phone for 2+ minutes — the screen never sleeps. Unplug the
    camera and wait for the status to leave "Live": the screen now sleeps on its normal timeout.
15. **Overnight survival (BG-1, BG-6, BG-9):** grant the exemption and enable the crying alarm
    and the watchdog (so the watchdog is armed — WATCH-9), then leave the phone unplugged,
    stationary and screen-off for 2+ hours overnight. In the morning audio is still live (or has
    reconnected by itself), and the watchdog never fired spuriously.
16. **Restart (BG-10):** while monitoring, reboot the phone. After boot a notification says
    monitoring stopped; tapping it opens the app and monitoring resumes. The "monitoring
    stopped" notification is gone once monitoring runs again — also when the app was opened
    from the launcher instead of the notification.
17. **Silent stall (WATCH-7):** while live, switch the camera off (do not touch the phone's
    network). Within seconds the status leaves "Live" and reconnect attempts begin. Leave it off
    and reopen the app: it never claims "Live" while the camera sends nothing. With the watchdog
    armed (its toggle and the crying alarm on), its alarm rings after the grace period.
18. **Session expiry (BG-8, AUTH-8):** sign in, then invalidate the session server-side (sign out of
    the Mi account elsewhere / revoke the device). The status and notification say the session
    expired rather than looping "Connection lost", and opening the app lands on sign-in. Then the
    opposite: pull the phone off Wi-Fi entirely — it must keep retrying and must NOT sign you out.
    Finally, sign back in with good credentials: the new session sticks — the app must not bounce
    back to sign-in claiming the fresh session expired.
18b. **Reopen never restarts (BG-5):** while live, leave the app, swipe it out of recents, and
    reopen it — the feed is live immediately, with no reconnect blip in the status or logs
    (`adb logcat -s BabyMonitor` shows no new "connecting" for the ongoing session). Stop
    monitoring from the notification, reopen the app — monitoring starts again.
19. **Mute keeps the alarm working (LIVE-3), and is obvious (LIVE-2):** mute the feed — the mute
    button draws latched (filled, attention-coloured) and the status line gains "· muted"; there
    is no way to misread it as "press to mute". Then make noise at the camera — the level
    indicator still moves and the noise alarm still fires; only the speaker is silent. Unmute —
    the latched look and the "· muted" both clear.
20. **Night vision (LIVE-10):** open the night-vision control — it shows the camera's current mode.
    Pick another mode; the camera's infrared switches accordingly and the choice sticks after
    reopening. Unplug the camera and try again: a readable error appears and the shown mode does
    not change.
21. **Camera list failures (CAM-5):** with the phone offline, open the camera picker — a readable
    error with a Retry. On an account with no cameras, it says so rather than showing an empty list.
22. **The alarm sounds (ALRM-11, ALRM-14):** in settings, preview every sound — each is audible and
    clearly different from the others; each alarm previews at its own set volume and vibrates with
    the preview when that alarm's vibrate is on. Both alarms may be given the same sound. Trigger a
    real alarm: it starts softer and climbs to that alarm's volume within a few seconds, and the
    phone vibrates if that alarm's vibrate is on. Turn the alarm's volume down and preview again —
    quieter, not silent.
23. **Trigger point on the level meter (ALRM-12):** with the crying alarm on, watch the level bar —
    a mark shows where the alarm triggers, and the bar changes colour once the room is past it.
    Answer "no" to a feedback question (item 25) and check the mark moves right by one step.
24. **Crying vs everything else (ALRM-3, ALRM-13):** the real test. Set the sensitivity to maximum
    (10). Then, at the camera: hold a normal conversation in the next room with the door shut;
    play a TV through the wall; run a fan; slam a door; talk in the room in an adult voice.
    **None of them may alarm.** Then let the baby cry (or play a recording of a baby crying, in
    the room): it alarms within a couple of seconds.
25. **Learning from answers (ALRM-15, ALRM-16, ALRM-17):** trigger a crying alarm and acknowledge
    it in the app — "Was the baby crying?" appears; it can be dismissed without consequence.
    Trigger again, answer **No** — settings now show the camera tuned one step stricter, and the
    same sound at the same volume no longer alarms. Let a louder cry alarm, answer **Yes** — the
    step is undone. Trigger a feed-drop alarm (kill Wi-Fi) and acknowledge — no question appears.
    Acknowledge a crying alarm from the notification with the app closed — the question waits on
    the viewer the next time the app opens. Kill and reopen the app — learned tuning survives;
    Reset in settings clears it and the trigger mark returns to the slider's own point.


---

# macOS checklist

Run on a real Mac with a reachable camera. The shared behaviour (crying detection, reconnect,
watchdog, alarm timing) is not re-verified here — it is the same code, and the same tests run on
this platform. What follows is the shell, and the honesty about what a Mac cannot do.

M1. **Menu bar is the app (MACOS-1, MACOS-2):** launch it. A menu bar item appears and its icon
    shows the state; the menu names the camera and reads live/reconnecting/stopped in words.
    Unplug the camera — the icon and the menu both change within seconds.

M2. **Closing a window is not quitting (MACOS-7, MACOS-9, BG-5):** with audio playing, close the
    main window. Audio keeps playing, the menu bar item stays. Reopen from the menu — the feed is
    live immediately, with no reconnect in the log (`log stream --predicate 'process ==
    "BabyMonitor"'` shows no new "connecting"). Only Quit ends the app.

M3. **Mute keeps the alarm working (MACOS-4, LIVE-3):** mute from the menu bar. The speaker goes
    silent, the menu says muted. Make noise at the camera — the level indicator still moves and,
    with the alarm on, it still rings. Only the speaker was silent.

M4. **Mini window floats (MACOS-5, BG-7m):** show the mini window. It sits on top of other apps,
    stays visible when another app is full-screened, and follows across spaces. Move and resize
    it, quit and relaunch — it comes back where it was. Close it — monitoring carries on.

M5. **Stop and start (MACOS-3, BG-11):** Stop from the menu — it asks for confirmation; cancel and
    nothing changes; confirm and audio, alarm and connection all stop. Start again from the menu.

M6. **Alarm audibility (ALRM-4, ALRM-10):** mute the feed, turn the Mac's output volume down, and
    play a crying clip at the camera for ~3 s. It is still audible, it rings until acknowledged,
    and the menu bar icon is unmistakable while it rings.

M7. **Idle sleep is held off (BG-12, MACOS-10):** set the Mac to sleep after 1 minute of
    inactivity. Start monitoring and leave it alone for 5 minutes without touching anything. The
    Mac does not sleep and audio never stops. Stop monitoring — the Mac sleeps normally again.

M8. **The lid is the honest one (BG-12, MACOS-11):** this is the step that matters most, because
    it is where the Mac is weaker than the phone and must say so.
    a. Before relying on it overnight, the app states plainly that a closed lid stops the monitor.
       Find that message. If a parent could miss it, it is not good enough.
    b. While monitoring, close the lid for 2 minutes. Open it. The app reports that monitoring was
       **down**, and for **how long** — it does not simply reconnect as though nothing happened.
    c. With the watchdog armed, the sleep gap is treated as a real outage (WATCH-2), not as a live
       feed that happened to be quiet.

M9. **Restart (BG-13, MACOS-8):** while monitoring, restart the Mac. On next launch the app says
    monitoring stopped and resumes in one click. Turn on "open at login" and restart again — it
    comes back by itself.

M10. **Updates never interrupt (UPD-3, UPD-5, UPD-7):** with monitoring running, publish a newer
     release. The app downloads and verifies it, says it is ready, and **does not restart**.
     Monitoring is untouched. Stop monitoring — the update applies. Confirm the new version under
     About (UPD-6, LIVE-15).

M10b. **The update does not ask for a password (AUTH-6m):** the step above must complete with **no
     Keychain prompt at all** — the updated app reads its own stored session in silence and comes
     straight back up live. If a password box appears, the signing is wrong (certificate,
     provisioning profile and entitlement are a set — see CLAUDE.md), and an overnight update would
     leave the monitor stopped behind a dialog nobody is awake to answer. This is the single most
     important step on this list.

M11. **A dead updater says so (UPD-4, UPD-8):** revoke the token the updater uses. Within a few
     check cycles the app reports that it can no longer check for updates. Monitoring is
     completely unaffected. Restore the token — the complaint clears.
