# Live feed

The main screen: the selected camera's live video and audio, with mute and a visible connection
state. Monitoring starts when the live feed opens and keeps running until explicitly stopped
(see background spec).

- **LIVE-1** `[device]` The live feed plays the camera's video and audio together.
- **LIVE-2** One tap mutes/unmutes playback audio. The mute state is persisted and applies on the
  next launch. While muted, the live feed says so at a glance: the status line reads muted, and
  the mute control shows an engaged state — a changed icon glyph is never the only clue, so
  "muted" can never be misread as "press to mute".
  (A desktop's mini shape is too small to spend a word of its status line on this, so there the
  **always-visible, latched mute control** is what says it — DESK-8. The rule it must satisfy is
  the same one: never a changed glyph alone.)
- **LIVE-3** `[device]` Mute silences the phone's playback only — level monitoring and the noise alarm keep
  working exactly as when unmuted.
- **LIVE-4** The connection state is always visible on the live feed: connecting, live,
  reconnecting (with countdown), or a readable error. **Live** means audio is arriving — a
  connection delivering no usable audio is never shown as live (WATCH-7). Video may render
  moments before the first audio arrives; the status stays "connecting" until audio proves the
  feed.
- **LIVE-5** A dropped stream reconnects automatically with increasing waits between attempts
  (starting under a second, capped at tens of seconds); a successful connection resets the
  backoff. No user action is ever needed to resume.
- **LIVE-12** A failure that retrying can never fix — the selected camera's protocol is not
  supported, or its audio arrives only in a format that cannot be played — is said in plain
  words and is not retried; it is never dressed up as a connection problem that will resolve
  itself.
- **LIVE-6** A live level indicator shows the room's loudness relative to its own ambient
  baseline: zero means "as loud as this room usually is". Sustained background — room tone, a
  fan or white-noise machine, air conditioning — is the baseline, **including its natural
  flutter**: a steady source that wavers by a few dB still reads flat, at or near zero, and on
  its own never reads loud enough to reach the crying alarm's loudness bar even at the highest
  sensitivity (ALRM-2). What little flutter remains is not shown as activity: displayed levels
  below a small residual (about 2 dB) read as zero — while detection and diagnostics keep the
  unrounded value, so no sensitivity is traded for the calmer display. A new steady source
  settles into the baseline within about a minute.
  The onset of genuinely louder sound reads immediately as a raised level — and real crying,
  bursts with breaths between them, keeps reading loud for as long as it goes on: the quiet
  breaths keep the baseline anchored to the room, so an ongoing cry is not absorbed into
  "usual" and can still re-alarm after an acknowledgment (ALRM-5).
- **LIVE-7** `[device]` Video trouble never takes audio down: if video cannot be decoded or
  rendered, audio monitoring continues unaffected.
- **LIVE-8** The feed tracks real time — delay never accumulates. When playback falls behind
  (startup burst, network hiccup, slow decoding), the app skips forward: video drops the backlog
  and resumes at the next clean entry point, audio drops its backlog — a brief glitch, not a
  growing lag. Audio and video never stall each other.
- **LIVE-9** `[android]` `[device]` The live feed is landscape only: opening it turns the display to
  landscape no matter how the phone is held, and leaving it lets the phone rotate freely again.
  The video fills the screen and the controls are icon buttons overlaying it in two rows —
  status and level indicator along the top, buttons along the bottom. Less-used actions —
  switching camera, signing out, About — sit behind a menu at the top right instead of being
  always-visible buttons. An unacknowledged alarm's acknowledge control is always visible.
- **LIVE-16** `[desktop]` `[device]` A desktop has no orientation to lock, so the same intent lands
  as a window: video edge to edge, the same two rows of overlaid controls, the same rare actions
  behind a menu, the same always-visible acknowledge (DESK-7). Which controls appear, and when, is
  the shared decision (BG-11, BG-14, WATCH-11) — the phone's button row and the desktop's cannot
  disagree.
- **LIVE-11** `[android]` `[device]` The controls are shown or hidden **only** by tapping the **video**: each
  tap toggles them. They never hide on their own — a parent watching the room is never made to
  tap to get them back. Tapping the controls themselves uses the control and never hides them:
  nothing can make them vanish under the user's own finger.
- **LIVE-17** `[desktop]` `[device]` A desktop has a pointer, so the controls follow it instead of a
  tap: they are there whenever the pointer is over the window, and they fade away a few seconds after
  it leaves or stops moving — any movement brings them straight back, with no click needed. What the
  feed is *doing* never fades: **the status line, the level indicator, and any warning or ringing
  alarm are always on screen**, whatever the pointer is doing. The rule is the phone's rule
  (LIVE-11) against the same hazard: a parent must never have to go looking for the state of the
  monitor, and a control that hides is never one that matters.
- **LIVE-13** `[device]` The camera is reachable only on its own network. While the device has no
  connection to that network, the live feed warns in plain words that the camera cannot be
  reached without it, and offers the shortest route to fixing it: tapping or clicking the warning
  opens the system's own network settings (on Android, the Wi-Fi panel). The warning disappears once
  the network is back. (A desktop may sit on the camera's network over Ethernet, so it warns about
  having *no* network rather than about not being on Wi-Fi: the hazard is a camera that cannot be
  reached, not a particular radio being off.)
- **LIVE-14** `[device]` While the live feed is on screen and the feed is live, the display stays
  awake — watching the baby never ends in a sleeping screen. When the feed is not live (or the
  live feed is left), the display may sleep normally again. On a desktop this is the display only;
  keeping the *machine* from suspending the monitor is BG-12, and is a separate promise.
- **LIVE-15** `[device]` The app shows the running version (on the live feed's menu, under About)
  — enough to tell at a glance whether an update landed (UPD-6).
- **LIVE-10** `[device]` The live feed has a night-vision control offering the camera's three modes — off,
  auto, and on. It shows the camera's current mode (read when the feed opens) and, on change,
  sets the mode on the camera. Because the mode lives on the camera, it is shared by everyone
  viewing it. A read or write that fails (camera offline, etc.) shows a readable error and leaves
  the displayed mode unchanged; the actual `[device]` infrared switch is the camera's.
