# Live feed

The main screen: the selected camera's live video and audio, with mute and a visible connection
state. Monitoring starts when the live feed opens and keeps running until explicitly stopped
(see background spec).

- **LIVE-1** `[device]` The live feed plays the camera's video and audio together.
- **LIVE-2** One tap mutes/unmutes playback audio. The mute state is persisted and applies on the
  next launch. While muted, the live feed says so at a glance: the status line reads muted, and
  the mute control shows an engaged state — a changed icon glyph is never the only clue, so
  "muted" can never be misread as "press to mute".
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
- **LIVE-9** `[device]` Controls are icon buttons. In portrait they sit below the video together
  with the level indicator and status. In landscape the video fills the screen and the controls
  overlay it in two rows — status and level indicator along the top, buttons along the bottom.
  An unacknowledged alarm's acknowledge control is always visible.
- **LIVE-11** `[device]` In landscape the controls are shown or hidden **only** by tapping the
  **video**: each tap toggles them. They never hide on their own — a parent watching the room is
  never made to tap to get them back. Tapping the controls themselves uses the control and never
  hides them: nothing can make them vanish under the user's own finger.
- **LIVE-10** `[device]` The live feed has a night-vision control offering the camera's three modes — off,
  auto, and on. It shows the camera's current mode (read when the feed opens) and, on change,
  sets the mode on the camera. Because the mode lives on the camera, it is shared by everyone
  viewing it. A read or write that fails (camera offline, etc.) shows a readable error and leaves
  the displayed mode unchanged; the actual `[device]` infrared switch is the camera's.
