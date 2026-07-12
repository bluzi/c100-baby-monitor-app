package com.bluzi.babymonitor.monitor

import com.bluzi.babymonitor.data.Settings
import com.bluzi.babymonitor.dsp.CRY_PITCH_MAX_HZ
import com.bluzi.babymonitor.dsp.CRY_PITCH_MIN_HZ
import com.bluzi.babymonitor.dsp.WindowMetrics

// ALRM-1/2/3/5/13: decides when a sound is the baby crying. Pure logic — audio analysis feeds
// it windows of metrics; it answers "alarm now?". Clock injected via nowMs. There is exactly one
// algorithm (ALRM-2): the only tunables are the loudness threshold — set from the sensitivity
// dial plus the learned per-camera steps (CryCalibration.kt) — and nothing else.
//
// Ring/acknowledge lifecycle (ALRM-4/5): when a trigger starts the ringer, the engine sets
// [suppressed]; on acknowledgment it clears it and calls [snooze] with now + cooldown.

/** A cry is a clear, sustained pitch. Fans, white noise, rumble and bangs never reach this. */
private const val MIN_TONALITY = 0.30

/** A cry in the room keeps harmonics above 1 kHz. Through a wall or door, they are gone. */
private const val MIN_BRIGHTNESS = 0.12

/** Rumble — and the muffled remains of next-door voices — pile energy below 300 Hz. A cry doesn't. */
private const val MAX_LOW_RATIO = 0.55

/**
 * Is this window the sound of a baby crying *in this room* (ALRM-3)?
 *
 * Every clause rejects one real thing (ALRM-13):
 *  - loud enough    → the room's own quiet
 *  - pitch in range → adults, in ANY room: their fundamental is an octave or more below a baby's
 *  - tonal          → fans, white noise, rumble, door slams (no stable pitch at all)
 *  - bright         → speech and TV through a wall (a wall is a low-pass filter)
 *  - not bass-heavy → traffic rumble, and again the muffled remains of next-door voices
 *
 * Loudness alone cannot do this: at a high sensitivity, conversation next door is louder than the
 * baby. Pitch alone cannot either — a fan has no pitch but neither does a slam. Together they can.
 * Only the loudness clause is tunable (sensitivity + learning); the character gates never move.
 */
fun isCryLike(levelDb: Double, thresholdDb: Double, m: WindowMetrics): Boolean =
    levelDb >= thresholdDb &&
        m.pitchHz >= CRY_PITCH_MIN_HZ &&
        m.pitchHz <= CRY_PITCH_MAX_HZ &&
        m.tonality >= MIN_TONALITY &&
        m.brightness >= MIN_BRIGHTNESS &&
        m.lowRatio <= MAX_LOW_RATIO

class BabyNoiseDetector(
    @Volatile var enabled: Boolean = false,
    @Volatile var thresholdDb: Double = effectiveThresholdDb(Settings.SENSITIVITY_DEFAULT, 0),
    // ALRM-3 sustain parameters
    private val sustainMs: Long = 2000,
    private val minCoverage: Double = 0.6,
) {
    private data class Window(val atMs: Long, val durationMs: Long, val isCry: Boolean)

    private val recent = ArrayDeque<Window>()

    /** True while an alarm is sounding unacknowledged — nothing new may trigger (ALRM-5). */
    @Volatile
    var suppressed = false

    private var snoozeUntilMs = Long.MIN_VALUE

    /** Quiet period after an acknowledgment (ALRM-5). */
    fun snooze(untilMs: Long) {
        snoozeUntilMs = untilMs
    }

    /** Feed one analysis window. Returns true when the alarm should start ringing now. */
    fun onWindow(levelDb: Double, metrics: WindowMetrics, windowMs: Long, nowMs: Long): Boolean {
        val cryLike = isCryLike(levelDb, thresholdDb, metrics)
        recent.addLast(Window(nowMs, windowMs, cryLike))
        while (recent.isNotEmpty() && recent.first().atMs < nowMs - sustainMs) recent.removeFirst()

        if (!enabled || suppressed || nowMs < snoozeUntilMs) return false

        val fired = cryTrigger(nowMs)
        if (fired) recent.clear()
        return fired
    }

    /** ALRM-3: cry-like sound held across the sustain span. A door slam is loud but over in 50 ms. */
    private fun cryTrigger(nowMs: Long): Boolean {
        val span = nowMs - recent.first().atMs + recent.first().durationMs
        if (span < sustainMs) return false // judge only once a full span has been observed
        val cryMs = recent.filter { it.isCry }.sumOf { it.durationMs }
        return cryMs >= minCoverage * sustainMs
    }
}
