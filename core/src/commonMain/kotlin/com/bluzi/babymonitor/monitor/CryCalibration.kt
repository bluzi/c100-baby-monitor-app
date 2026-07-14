package com.bluzi.babymonitor.monitor

import com.bluzi.babymonitor.data.Settings

// ALRM-2 / ALRM-15/16/17: the single sensitivity dial and the per-camera learning behind it.
// Pure logic — no android imports.
//
// Sensitivity (1..10, middle by default) is the ONLY detection control the user has. It maps to
// how loud a sound must be (dB above the room's own baseline) before the cry gates in
// BabyNoiseDetector are even consulted. The character gates themselves (pitch, tonality,
// brightness) never move: whatever the dial or the learning says, a fan or next-door talk can
// never become "the baby" (ALRM-13) — and, just as deliberately, learning can never redefine the
// baby's own sound away. All either can change is the loudness bar.
//
// Learning is asymmetric by nature: parents tell us about false alarms, but a MISSED cry is
// silent — nobody files a report at 3am for the alarm that didn't ring. Unbounded "stricter"
// learning would therefore slowly deafen the monitor. Hence hard bounds: at most
// [CALIBRATION_MAX_STEPS] steps above the slider, never below it, and always visible and
// resettable in settings (ALRM-17).

/** One learning step, in dB — also the loudness distance between two neighbouring slider stops. */
private const val STEP_DB = 2.0

/** ALRM-16: learning stops here. At worst the baby must be ~6 dB louder — never inaudible. */
const val CALIBRATION_MAX_STEPS = 3

/** ALRM-2: the loudness bar for a sensitivity setting, in dB above the room's baseline. */
fun sensitivityThresholdDb(sensitivity: Int): Double =
    LevelMeter.LEVEL_MAX - STEP_DB * sensitivity.coerceIn(Settings.SENSITIVITY_MIN, Settings.SENSITIVITY_MAX)

/**
 * ALRM-16: the bar actually in force — the slider's bar plus the learned steps, clamped to the
 * top of the level scale so the trigger point always stays where it can be seen and tuned.
 */
fun effectiveThresholdDb(sensitivity: Int, falseAlarmSteps: Int): Double =
    (sensitivityThresholdDb(sensitivity) + STEP_DB * falseAlarmSteps.coerceIn(0, CALIBRATION_MAX_STEPS))
        .coerceAtMost(LevelMeter.LEVEL_MAX)

/** ALRM-16: "no, false alarm" — one step harder to trigger, up to the cap. */
fun afterFalseAlarm(steps: Int): Int = (steps + 1).coerceIn(0, CALIBRATION_MAX_STEPS)

/** ALRM-16: "yes, real cry" — undo one step, never below the slider's own setting. */
fun afterRealCry(steps: Int): Int = (steps - 1).coerceIn(0, CALIBRATION_MAX_STEPS)

/** ALRM-15: only the crying alarm asks for an answer — a feed-drop alarm is not a detection. */
fun asksForCryFeedback(kind: AlarmKind): Boolean = kind == AlarmKind.BABY_NOISE
