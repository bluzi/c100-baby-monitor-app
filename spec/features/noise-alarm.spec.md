# Crying alarm

An optional alarm that sounds on the phone when the camera hears the baby crying.

The hard part is not hearing sound — it is telling **a baby crying in the room** apart from
everything else a nursery hears at night: the parents talking in the next room, a television
through the wall, traffic, a fan, a door. A monitor that cries wolf gets muted, and a muted
monitor is no monitor at all.

- **ALRM-1** Settings (reachable from the live feed) has a crying-alarm toggle, **off** by default.
- **ALRM-2** How easily the alarm triggers is one **sensitivity** setting: a slider from 1 to 10,
  defaulting to the middle. Higher sensitivity triggers on quieter crying; lower sensitivity needs
  louder crying. There are no detection modes and no other detection knobs. Changes take effect
  immediately.
- **ALRM-12** `[device]` While adjusting the sensitivity, the current live room level is shown on
  the same scale, together with the loudest level heard since the settings were opened (reset on
  each open), so it can be tuned against the real room. The level indicator marks the point where
  the alarm would trigger — including any learned adjustment (ALRM-16) — and reads differently
  (colour) once the room is loud enough to be over it, so it is obvious at a glance whether the
  current sound would alarm.

## What counts as crying

- **ALRM-3** The alarm triggers only on sound that is all of:
  - **loud enough** — as loud as the sensitivity setting demands;
  - **sustained** — held for about two seconds, brief dips allowed;
  - **pitched like an infant** — its fundamental pitch is in the range of a baby's cry (roughly
    250–700 Hz), which is far above an adult speaking voice;
  - **strongly tonal** — a clear repeating pitch, not a hiss or a rumble;
  - **present in the room** — bright, with the high frequencies intact. Sound coming through a
    wall or a closed door loses its high frequencies and is not treated as crying.

  With the toggle off, nothing triggers regardless of level or sensitivity.
- **ALRM-13** These do **not** trigger the alarm, at **any** sensitivity, including the most
  sensitive setting: adults talking in the next room or behind a door (muffled and low-pitched);
  an adult voice in the room (too low-pitched to be a baby); traffic or appliance rumble; a fan or
  white noise; a single door slam or other brief bang.

## Learning from the parent

The detector tunes itself to this baby and this camera, from the only ground truth there is:
the parent's own judgment of each alarm. Because a missed cry is silent — no one reports it —
learning may only ever nudge, within hard bounds, and never in secret.

- **ALRM-15** After a **crying** alarm is acknowledged, the app asks whether the baby was really
  crying (**yes** / **no**). Answering is optional: the question can be dismissed or simply
  ignored, it never blocks anything and never sounds anything, and it is never asked for a
  feed-drop alarm. Only the most recent unanswered alarm is asked about, and a ringing alarm
  always takes its place.
- **ALRM-16** Answers tune detection **for that camera**: a **no** (false alarm) makes triggering
  one small step harder; a **yes** undoes one step. The tuning is bounded — it can never move
  more than a few steps from the slider setting, and never below it (answers never make the
  monitor *more* sensitive than the parent chose). It adjusts only how loud a sound must be:
  the sounds that never trigger (ALRM-13) are untouched by any amount of tuning, in either
  direction.
- **ALRM-17** The learned tuning is kept per camera, persists across restarts, and is visible in
  settings — with a way to reset it for the current camera. The sensitivity slider stays the
  primary control: learned tuning rides on top of whatever the slider says, and moving the slider
  does not erase it.

## Sounding the alarm

- **ALRM-4** `[device]` A triggered alarm sounds a **repeating** alarm tone on the phone —
  audible even when the live feed is muted — and posts a notification. It keeps sounding until
  acknowledged from inside the app or from the notification; it never times out on its own. If
  the phone cannot produce the tone at all, that failure is never silent: monitoring keeps
  running and the notification still appears.
- **ALRM-11** The alarm sound is configurable, separately for the crying alarm and for the
  feed-drop alarm (WATCH-2), from a set of choices ranging from calm to urgent. The two alarms can
  never be given the same sound — they demand different reactions, so they must never be confused.
  Settings also has an alarm volume and a vibrate option, and every alarm sound can be **previewed**
  from settings (a preview stops on its own and never counts as a real alarm).
- **ALRM-14** An alarm starts at a gentler volume and rises to the configured volume within a few
  seconds — enough to wake without startling, and never so gentle that it fails to wake.
- **ALRM-5** While an alarm is sounding, no new alarm can trigger. After acknowledgment the alarm
  stays quiet for a 30-second cooldown, then may trigger again.
- **ALRM-6** The alarm toggle, sensitivity, schedule, sounds, volume and vibrate setting all
  persist across restarts. A threshold saved by an older version of the app maps onto the
  sensitivity scale rather than being lost.
- **ALRM-7** The alarm can be scheduled: active **always** (default), or only between two daily
  times — a window that may cross midnight (e.g. 19:00–07:00). Outside the window nothing
  triggers. A window whose start equals its end means always. Schedule changes take effect
  immediately.
- **ALRM-10** `[device]` An alarm nobody can hear is not an alarm: if the phone's alarm volume is
  turned down, it is raised for the duration of the alarm and put back to what the user had once
  the alarm is acknowledged — including after the app is killed while ringing. If the user
  themselves changed the alarm volume in the meantime, theirs wins and nothing is put back.
