# Device checklist

Manual verification for `[device]` criteria — run against a real, reachable C100 camera before
calling a release done. Each step names the criteria it verifies.

Steps 1–25 are the **phone** checklist. Most are `[mobile]` and run on **both** phones — Android and
iOS. A handful are marked **(Android)** in the step title because they exercise an Android-specific
surface: the persistent notification (3) — the Android face of the shared status surface (BG-2/BG-3),
where iOS wears a Live Activity — plus the lock-screen view (12), the alarm-stream volume (13), the
battery-optimisation exemption (14), and the reboot notification (16), which are genuinely
Android-only behaviours. Their iOS counterparts — which answer the same hazards differently — are in
the **iOS addendum** after step 25. The **desktop** checklist is last, run
**twice — once on a Mac, once on a PC**. The monitor itself behaves the same everywhere, proven by the
same spec suite on every platform, so what needs a human is each shell — and the places where one
platform can do less than another.

1. **Live playback (LIVE-1):** open the app (signed in, camera selected). Video renders and
   room audio is audible within a few seconds.
2. **Background + lock (BG-1, BG-4):** with audio playing, press home, then lock the phone, then
   let the screen sleep for 5+ minutes. Audio never stops. Make a sharp noise at the camera with
   the alarm enabled and the feed muted — the phone alarms.
3. **(Android) Notification (BG-2, BG-3):** while monitoring, check the persistent notification names the
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
8c. **The icon (UI-3):** find the app in the launcher, in recents, and in Android's app settings —
    it shows the app's own mark (the waveform on its night-blue field), never a placeholder. With a
    themed-icon launcher (Android 13+), it tints cleanly rather than turning into a blob. Hold it
    against the Mac's icon in the Dock: **it is the same mark.**

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
12. **(Android) Lock screen (BG-7):** while monitoring, lock the phone and tap the monitoring
    notification (or the launcher icon) — the live feed shows without unlocking.
12b. **(Both phones) Picture-in-picture (BG-18):** with the live feed showing, switch to another app
    (the home gesture / recents on Android; the home gesture on iOS) — the video keeps playing in a
    small floating window over that app, with no controls on it. Tap the window to return: the feed is
    back inline, uninterrupted, and the audio and the crying alarm never stopped throughout. On a
    device whose OS has no PiP, the app simply keeps monitoring in the background as before — no float,
    no gap in the watch.
    **On Android, test this with the phone on gesture navigation** (the modern default), not just
    three-button — the swipe-up-home path relies on the OS auto-entering PiP, and it is the one that
    silently breaks while three-button Home still works.

12c. **(Both phones) Picture-in-picture can be turned off (BG-19):** it is **on by default**. In
    settings, turn "keep the video floating when you leave the app" off, then leave the app on a live
    feed — it now just backgrounds like any other app, **no floating window**, and the audio and the
    crying alarm keep running throughout. Turn it back on and repeat 12b — the window floats again.

12d. **(Both phones) Unavailable PiP is said, not silently offered (BG-20):** revoke the app's PiP
    permission — on Android, Settings → Apps → Baby Monitor → Picture-in-picture → off; on iOS, run on
    the Simulator, where PiP does not exist. Open the app's settings: the switch is **off and greyed
    out**, and the text says why (on Android it points at the system setting; the "Open picture-in-picture
    settings" button jumps there, and turning it back on re-enables the switch on return).
13. **(Android) Alarm audibility at zero volume (ALRM-10):** turn the phone's alarm volume all the way down,
    then trigger the noise alarm — it is still audible, and the volume is put back where the user
    had it after acknowledging. Repeat, but force-stop the app while it rings: on next launch the
    volume is still put back. Repeat once more and, while it rings, change the alarm volume
    yourself — after acknowledging, *your* setting is the one that survives.
13b. **Camera moved (WATCH-8, LIVE-5):** while monitoring, reboot the router so the camera comes
    back on a different IP. The app keeps retrying, picks up the new address, and returns to live
    on its own — it never sits on "Connecting…" indefinitely.
14. **(Android) Battery exemption (BG-9):** with the app not exempt from battery optimisation, the live feed
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
16. **(Android) Restart (BG-10):** while monitoring, reboot the phone. After boot a notification says
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

# iOS addendum

Run on a real iPhone with a reachable camera, **after** the shared phone steps above (1–25, skipping
the ones marked *(Android)*). The shared behaviour — crying detection, reconnect, watchdog, alarm
timing — is not re-verified here; it is the same code and the same tests. What follows is the iOS
shell, and the honesty about what an iPhone cannot do.

I1. **The app looks like an iOS app (IOS-1, UI-1, UI-3, LIVE-9):** find it on the Home Screen, in the
    App Library, and in the app switcher — it shows the app's own mark, never a placeholder, and it is
    **the same mark the Mac and the phone show** (UI-3). Every screen renders dark. The live feed is
    landscape however the phone is held; sign-in, the camera picker and settings are portrait.

I2. **Paste and autofocus (IOS-2, AUTH-11):** on sign-in, paste a Mi password from a password manager
    into the password field — it pastes (no Edit menu needed). The screen opens with the keyboard up
    on the first field; sign in so Xiaomi asks for a code — the code field is focused the same way.

I3. **The Live Activity and Dynamic Island (IOS-3, BG-2, WATCH-4):** start monitoring, then lock the
    phone. A **Live Activity** shows the camera and reads **live**; on an iPhone with a Dynamic Island,
    the island shows it too. Kill Wi-Fi — both read **reconnecting/error** within seconds. Trigger an
    alarm — the island and the lock-screen card become **unmistakable** (colour + bell). Open Control
    Center — Now Playing names the camera.

I4. **Stop from the Live Activity (IOS-3, BG-3):** from the lock screen's Live Activity, use **Stop** —
    audio and connection stop **without opening the app**, and the Live Activity clears. Reopen the app
    — it offers Start (the in-app control, BG-11), not a dead end.

I5. **Background survival + interruptions (BG-1, BG-6, BG-9i, IOS-4):** with audio playing, press home,
    lock, and let the screen sleep 5+ minutes — audio never stops; make a sharp noise with the alarm
    on and the feed muted — it alarms. Toggle Wi-Fi off/on while locked — audio resumes on its own
    within a minute. Take a phone call — the feed pauses, and resumes by itself when the call ends.
    Then start playing music in another app that seizes audio for good — the app reports **monitoring
    stopped** rather than sitting silent (WATCH-11).

I6. **Alarm audibility and notifications (IOS-5, ALRM-4, ALRM-10i):** on first run, **deny**
    notification permission, then trigger an alarm — it **still sounds and vibrates**, and the app says
    the notification will not appear. Grant permission and trigger again — a notification with an
    Acknowledge action appears. Flip the phone to **silent** (ring switch) and trigger — it **still
    rings** (the media session ignores the silent switch). Turn the phone's volume all the way down —
    the app says the alarm may be quiet; it does **not** claim to have made it loud (iOS forbids raising
    the volume).

I7. **The lock screen is honest (BG-7i, IOS-6):** while monitoring, lock the phone. You see the Live
    Activity — **not** the live video — and the app has never claimed you would. Unlock — the feed is
    there. A locked iPhone must be unlocked to see the baby, and the app says so where a parent looks.

I8. **Reboot and force-quit are honest (BG-10i, IOS-6):** while monitoring, swipe the app out of the
    switcher (force-quit). Reopen it — it says monitoring was **down** and resumes; it never came back
    as though the watch had continued. Repeat by restarting the phone — same result: no silent resume.

I9. **No self-update (UPD-2i, IOS-6, LIVE-15):** there is no update control anywhere in the app and it
    never restarts itself — updates are the App Store's. The running version is visible under About, so
    "did the update land?" is answerable at a glance.


---

# Desktop checklist

Run this **twice: once on a real Mac, once on a real PC**, each with a reachable camera. It is one
list because they are one shell (see [desktop-shell](features/desktop-shell.spec.md)) — where the
nouns differ, the step names both (menu bar / tray, Quit / Exit, ⌘ / Ctrl). A step that applies to
only one machine says so in its title; there is exactly one.

The shared behaviour (crying detection, reconnect, watchdog, alarm timing) is not re-verified here —
it is the same monitor, and the same spec suite runs against both platforms, criterion for
criterion. What follows is the shell, and the honesty about what a desktop cannot do.

Where a step asks you to prove "no reconnect happened", read the log:
`log stream --predicate 'process == "BabyMonitor"'` on a Mac,
`%LOCALAPPDATA%\BabyMonitor\babymonitor.log` on a PC. No new "connecting" line means no restart.

D1. **The status icon is the app, and it is quiet while it works (DESK-1, DESK-2, BG-15):** launch
    it. The status icon — menu bar item / tray icon — is the app's own waveform and **nothing else**
    (on a Mac, drawn in the bar's own colour: never a black blob, never a moon). Its menu names the
    camera and reads live in words. Unplug the camera: the menu goes to reconnecting within seconds
    and **the icon does not change** — the monitor is working on it, and an icon that flinches at
    every blip is one a parent stops reading. Now break it for real (sign out of the Mi account
    elsewhere, so the session expires): the icon changes, unmistakably, to say the monitor is no
    longer watching. Trigger an alarm: it changes again, to its own icon. Those two, and only those
    two, change it. The menu opens and dismisses like every other status menu on the platform (on a
    PC, with either mouse button).

D1a. **The menu says only what is true (DESK-2):** sign out. The menu now reads **"Not signed in"**
     and offers **Sign in** — Mute, Show camera and Mini window are *gone*, not dimmed. Click Sign
     in and the window appears. Sign in but do not choose a camera: the menu reads "No camera
     chosen" and offers Choose camera and Sign out. Choose one — the full menu comes back.

D1b. **The camera submenu (DESK-2, CAM-4):** with two cameras on the account, open the status menu →
     Camera. Both are listed and **the one being watched has a checkmark**. Pick the other one: the
     app switches to it within seconds (the status line names the new camera), and reopening the
     submenu shows the checkmark has moved. "Choose camera…" at the bottom still opens the picker.

D1c. **Sign-in is a dialog, and it carries its own way out (DESK-5):** sign out while the window is
     in its mini shape — the sign-in screen comes up **full**, never as a tile (on a Mac, as a
     borderless panel with no traffic lights). On it, and on the camera picker, there is a Quit / Exit
     control on the screen itself. Use it: the app goes **without asking**, because nothing was being
     monitored. (Closing that window instead only hides the app — which is right for a monitor, and is
     exactly why the way out has to be on the screen: a panel with no close button leaves nothing else
     to press.)

D2. **Closing a window is not quitting (DESK-13, DESK-6, DESK-14, BG-5):** with audio playing, close
    the window (on a PC, once with the X and once with Alt-F4). Audio keeps playing and the status
    icon stays. With the window closed, the switcher no longer shows the app — it has receded into
    the status area; reopen it from there and it is back, like any other app. The feed is live
    immediately, with **no reconnect in the log**. Only Quit / Exit ends the app.

D2a. **The full shape (DESK-7, LIVE-16):** the video fills the window edge to edge, with the status
     and the level indicator overlaid along the top and the buttons along the bottom. Switching
     camera, signing out and the version live behind the "…" menu rather than on the bar. Compare
     against the phone and the other desktop: **the same controls, in the same states** — if one
     offers Stop and another does not, one of them is wrong (that decision is shared code).

D3. **Mute keeps the alarm working (DESK-4, LIVE-3, LIVE-2):** mute from the status menu. The
    speaker goes silent and the menu says muted (a checked item). Make noise at the camera — the
    level indicator still moves and, with the alarm on, it still rings. In the window, the mute
    button draws latched and the status line gains "· muted". Only the speaker was ever silent.

D4. **The mini shape floats (DESK-8, BG-17):** put the window in its mini shape. **The very first
    time** (before it has ever been moved — clear the stored shell prefs if need be) it appears in the
    **bottom-right corner**, clear of the taskbar / menu bar, never in the middle of the screen. It
    sits on top of other apps, including a maximised one — and on a Mac, over a full-screened app and
    across spaces. Move and resize it (it keeps the video's shape), quit and relaunch — it comes back
    where it was, in the shape it was in. Close it — monitoring carries on. In **Settings → Mini
    window → Corner**, pick each of the four corners in turn: the tile snaps to that corner of the
    screen at once, clear of the edges. Then drag it elsewhere — it stays where you drop it (the
    corner is a starting point, not a leash).

D4a. **One window, two shapes (DESK-9, DESK-10, DESK-15):** with the feed live, switch full → mini
     and back, from the window's own control, from the status menu, and from the keyboard. Each
     time: **the picture never blacks out, audio never stutters, and the log shows no reconnect.**
     Move and resize each shape and switch back and forth: each remembers its own size and position,
     and still does after a relaunch. Hover the mini — its close and "make it full" controls appear;
     move the pointer away — they go, leaving the picture, the feed state, the **room-level bar** and
     **mute** (DESK-8) — the slim level bar along the bottom rises and falls with the room and shows the
     alarm's trigger mark, pointer or no pointer, and mute stays too because a tile has no room to say
     "muted" in words (LIVE-2, LIVE-6). Now trigger
     an alarm with the pointer nowhere near the tile: **Acknowledge is on it, without hovering**
     (DESK-8) — an alarm you have to go looking for the controls to silence rings longer than it
     should. Run the pointer back and forth across the tile's own buttons: it must not flicker, and
     must never fade out from under the pointer (DESK-10).
     While the tile is up, look for it in the switcher — Mission Control on a Mac, Alt-Tab and the
     taskbar on a PC: **it is not listed among the windows** (DESK-15). It is already on top of
     everything; it must not clutter the list of windows you are trying to see past. The full window,
     when open, *is* there. (On a Mac the *app* is still in Cmd-Tab and the Dock while only the tile
     is up — that is how you reach it, and it is right. On a PC the tile itself is what must not
     appear.)

D4b. **Switching to the tile does not leave it stuck bright (DESK-11):** with fading on, click the
     mini-window control in the full window and then **do not move the mouse at all**. The tile flies
     to the corner and, within a moment, fades — it does not sit at full brightness with its controls
     showing until you happen to hover it. (It did exactly that on the Mac: the window moved out from
     under a stationary pointer, so nothing ever told the app the pointer had left. Check it on both.)

D4c. **The mini fades, but never over a warning (DESK-11, DESK-18):** with the feed live and the
     pointer away, the mini goes translucent and the window underneath is readable through it; move
     the pointer over it — instantly solid. Turn fading off in settings — it stays solid. Turn it
     back on, set it faint, then **unplug the camera**: as soon as the feed is not live the mini goes
     fully opaque by itself and stays that way with the pointer nowhere near it. Same with a ringing
     alarm. Then turn the system's transparency off (Mac: Accessibility → Display → Reduce
     Transparency; PC: Accessibility → Visual effects → Transparency effects): no fading at all, and
     the app's surfaces draw solid.

D4d. **Controls follow the pointer (LIVE-17):** in the full shape, move the pointer over the video —
     the buttons are there. Move it away (or leave it still) for a few seconds — the buttons fade
     out, while the status line and the level indicator stay. Move the pointer — the buttons are back
     immediately, with no click. Trigger an alarm and repeat: the alarm banner and its Acknowledge
     never fade.

D4e. **Paste works (DESK-16):** on the sign-in screen, copy a password from a password manager and
     paste it into the password field — ⌘V on a Mac, Ctrl-V on a PC. It pastes. Cut, copy and
     select-all work beside it, Tab moves between fields and Enter submits. On a Mac the Edit menu
     lists them, and the standard menu bar is there while a window is focused: App (About, Settings
     ⌘,, Hide, Quit), Edit, View, Window — ⌘, opens settings from anywhere. On a PC, F11 goes full
     screen and Esc leaves it.

D4f. **It looks like a native app (DESK-17, DESK-18, UI-1, UI-3):** the app has its own icon
     everywhere the OS shows one — the switcher, the Dock / taskbar, the status area, the window, and
     the Finder / Explorer — never a generic placeholder, and **the same mark the phone shows** (UI-3).
     Hold the three side by side if you can. Every screen renders dark. Turn the system's animations
     off (Mac: Reduce Motion) — nothing animates.

D4g. **No black bars (DESK-12):** with the feed live, look at the edges of the picture in both
     shapes — the video fills the window, with no black band above or below it. Drag a corner: the
     window keeps the camera's shape. Go full screen: there the screen's shape wins and any unused
     area is black, which is expected. Sign out — the sign-in window is freely resizable again.

D4h. **Offline is said out loud (LIVE-13):** turn every network interface off (Wi-Fi *and*
     Ethernet). Within a couple of seconds the window warns that the machine is offline and that the
     camera can only be reached on its own network, and offers a link that opens the system's network
     settings. Turn the network back on — the warning goes by itself.

D5. **There is no Stop, and quitting asks (BG-14, DESK-3, BG-16):** look for a stop control — in the
    window, in the "…" menu, in the status menu. **There is none, in any state**, and there is no way
    to leave the app sitting there, alive, with the watch ended. Now quit while monitoring (⌘Q, the
    status menu's Quit / Exit, and the feed menu's — try each): it asks first, plainly, and says the
    baby will not be monitored. Cancel — audio carries on and nothing changed. Confirm — the app goes,
    and with it the watch. Reopen it — it is watching again within seconds, with no Start needed.

D5a. **Start exists only for a monitor that broke (WATCH-11, BG-14):** force the monitor into its
     failed state and check that **Start** appears, in the window and in the status menu — a monitor
     that failed on its own must be recoverable without quitting the app. Once it is live again,
     Start is gone.

D6. **Alarm audibility (ALRM-4, DESK-23):** mute the feed, turn the machine's output volume down, and
    play a crying clip at the camera for ~3 s. It is still audible, it rings until acknowledged, and
    the status icon is unmistakable while it rings. There is **no vibrate control anywhere** in
    settings — the setting is not offered rather than offered and ignored.

D7. **Idle sleep is held off (BG-12, DESK-20):** set the machine to sleep after 1 minute of
    inactivity. Start monitoring and leave it alone for 5 minutes without touching anything. It does
    not sleep and audio never stops. Quit — it sleeps normally again. (`pmset -g assertions` on a
    Mac, `powercfg /requests` on a PC, names the app for exactly as long as it should and no longer.)

D7a. **The display stays awake, but only while watched (LIVE-14, DESK-20):** set the display to sleep
     after 1 minute. With the window open and the feed live, leave the machine alone for 3 minutes —
     the screen stays on. Close the window (monitoring carries on): the screen now sleeps on its
     normal timeout. Reopen it and unplug the camera so the feed leaves "Live": the screen sleeps
     again.

D8. **Sleep is the honest one (BG-12, DESK-21):** the step that matters most, because it is where a
    desktop is weaker than the phone and must say so.
    a. Before relying on it overnight, the app states plainly that a sleeping machine stops the
       monitor. Find that message. If a parent could miss it, it is not good enough.
    b. While monitoring, sleep the machine for 2 minutes — close the lid, or Apple menu → Sleep /
       Start → Sleep. Wake it. The app reports that monitoring was **down**, and for **how long** —
       it does not simply reconnect as though nothing had happened.
    c. With the watchdog armed, the sleep gap is treated as a real outage (WATCH-2), not as a live
       feed that happened to be quiet.

D9. **Restart (BG-13, DESK-19):** while monitoring, restart the machine. On next launch the app says
    monitoring stopped and resumes in one click. The app offers — once, and without ever having
    turned it on for you — to start at login; decline it and it does not ask again, and settings still
    show it off. Turn it on in settings and restart again — the app comes back by itself and
    monitoring resumes. Turn it off — it does not.

D10. **Updates: at launch, and only at launch (UPD-3, UPD-5, UPD-7; on a PC also UPD-10):** with a
     newer release published, launch the app. It checks **once, at launch**, downloads and verifies
     the update, puts it in place — on a Mac on top of itself, on a PC beside itself (UPD-10) — while
     the running app **keeps watching, untouched**: audio plays on behind the dialog. Then it asks,
     **once**, whether to restart into it.
     Answer **Later**. Nothing happens: monitoring carries on, and **the app never asks again and
     never restarts on its own** — leave it running for hours, overnight if you can, with another
     release published, and it must still be the old version, still watching. The status menu and
     Settings say quietly that the new version is installed and runs at the next launch (UPD-7). The
     only thing that can start a check while it runs is a human (UPD-9).
     Then quit and reopen: it comes up on the **new** version, before monitoring starts. Check About
     (UPD-6, LIVE-15).

D10a. **Restart now (UPD-5):** repeat, and this time answer **Restart now** while monitoring is live.
      The app restarts within seconds, comes back on the new version, and **monitoring resumes by
      itself**. The outage is seconds long and you are standing there — which is the only condition
      under which this app restarts at all.

D10b. **A check on demand (UPD-9):** with no update available, choose "Check for updates…" from the
      status menu, from the live feed's "…" menu, and (on a Mac) from the app menu. Each one answers —
      it says the app is up to date rather than leaving you wondering whether the click did anything.

D10c. **The update does not ask for anything (AUTH-12):** the steps above must complete with **no
      prompt of any kind** — no Keychain password box on a Mac, nothing on a PC. The updated app reads
      its own stored session in silence and comes straight back up live. If a password box appears on
      the Mac, the signing is wrong (certificate, provisioning profile and entitlement are a set — see
      CLAUDE.md). A monitor that stopped at a dialog after an overnight update, with nobody awake to
      answer it, would be a monitor that failed exactly when it mattered. This is the single most
      important step on this list.

D10d. **Automatic updates can be turned off (UPD-11):** in Settings, switch **off** "Check for updates
      automatically". Quit and reopen with a newer release published: the app comes up on the **old**
      version and stages nothing — no launch check ran. Now choose "Check for updates…" by hand
      (UPD-9): it still finds, verifies and offers the update exactly as in D10. Turn the setting back
      **on**: the next launch checks again on its own. (The setting persists across relaunches, and
      exists only on the desktops — the phones have no such control.)

D11. **A dead updater says so (UPD-4, UPD-8):** block `api.github.com` at launch (e.g. add a
     `127.0.0.1 api.github.com` line to the hosts file — the monitor itself talks to Xiaomi, not
     GitHub, so this touches only the updater). At the next launch check the app reports that it can no
     longer check for updates. Monitoring is completely unaffected. Remove the block — the complaint
     clears.

D12. **`[windows]` No H.265 decoder (DESK-22):** on a PC **without** the HEVC Video Extensions
     installed (or with them removed), open the live feed. The app says in plain words that Windows
     cannot decode this camera's video and points at the free extension — and **audio keeps playing,
     the level meter keeps moving, and the crying alarm still fires.** Install the extension and
     reopen: the picture appears. This is the one place a PC is weaker than a Mac, and a black
     rectangle with no explanation would be exactly the kind of silence this app exists to prevent.
