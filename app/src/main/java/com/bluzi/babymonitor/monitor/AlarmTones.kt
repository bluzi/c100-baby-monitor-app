package com.bluzi.babymonitor.monitor

import com.bluzi.babymonitor.data.Settings
import kotlin.math.PI
import kotlin.math.exp
import kotlin.math.min
import kotlin.math.sin

// ALRM-11: the alarm sounds, synthesised rather than shipped as audio files — so they are pure,
// testable, and identical on every phone. Each one is a single loopable cycle: play it end to end,
// on repeat, and you have the alarm.
//
// The set runs from calm to urgent on purpose. A parent choosing "soft chime" for the baby wants
// to be woken gently; one choosing "siren" has slept through gentler things. What matters is that
// whichever they pick, the two alarms never sound alike (Settings.withSounds).

const val ALARM_SAMPLE_RATE = 22_050

/** How each sound is described to the user, calmest first. */
fun alarmSoundLabel(sound: String): String = when (sound) {
    Settings.SOUND_SOFT_CHIME -> "Soft chime"
    Settings.SOUND_RISING_CHIME -> "Rising chime"
    Settings.SOUND_LOW_PULSE -> "Low pulse"
    Settings.SOUND_URGENT_BEEP -> "Urgent beep"
    Settings.SOUND_SIREN -> "Siren"
    else -> sound
}

fun alarmSoundDescription(sound: String): String = when (sound) {
    Settings.SOUND_SOFT_CHIME -> "Gentle — wakes a light sleeper"
    Settings.SOUND_RISING_CHIME -> "Calm but insistent"
    Settings.SOUND_LOW_PULSE -> "Low and wrong-sounding — hard to mistake for a chime"
    Settings.SOUND_URGENT_BEEP -> "Sharp and hard to ignore"
    Settings.SOUND_SIREN -> "Loudest — for heavy sleepers"
    else -> ""
}

private fun note(
    out: ShortArray,
    startSec: Double,
    durationSec: Double,
    freqHz: Double,
    amplitude: Double,
    sampleRate: Int,
    /** Sine partials on top of the fundamental: a bare sine is easy to sleep through. */
    partials: Int = 2,
) {
    val start = (startSec * sampleRate).toInt()
    val n = (durationSec * sampleRate).toInt()
    for (i in 0 until n) {
        val idx = start + i
        if (idx < 0 || idx >= out.size) continue
        val t = i.toDouble() / sampleRate
        // Soft attack, exponential decay — a struck bell, not a click.
        val attack = min(1.0, t / 0.02)
        val decay = exp(-t / (durationSec * 0.45))
        var v = 0.0
        for (h in 1..partials) v += sin(2 * PI * freqHz * h * t) / h
        val sample = v * attack * decay * amplitude
        val mixed = out[idx] + (sample * 32767).toInt()
        out[idx] = mixed.coerceIn(-32768, 32767).toShort()
    }
}

private fun sweep(
    out: ShortArray,
    startSec: Double,
    durationSec: Double,
    fromHz: Double,
    toHz: Double,
    amplitude: Double,
    sampleRate: Int,
) {
    val start = (startSec * sampleRate).toInt()
    val n = (durationSec * sampleRate).toInt()
    var phase = 0.0
    for (i in 0 until n) {
        val idx = start + i
        if (idx < 0 || idx >= out.size) continue
        val p = i.toDouble() / n
        val hz = fromHz + (toHz - fromHz) * p
        phase += 2 * PI * hz / sampleRate
        val envelope = min(1.0, min(p / 0.05, (1 - p) / 0.05)) // no clicks at the ends
        val sample = sin(phase) * envelope * amplitude
        val mixed = out[idx] + (sample * 32767).toInt()
        out[idx] = mixed.coerceIn(-32768, 32767).toShort()
    }
}

/**
 * One loopable cycle of [sound] as 16-bit mono PCM. Every cycle ends with a gap: an alarm that
 * never pauses becomes a texture the brain filters out — the repetition is what wakes people.
 */
fun alarmPcm(sound: String, sampleRate: Int = ALARM_SAMPLE_RATE): ShortArray = when (sound) {
    Settings.SOUND_SOFT_CHIME -> {
        val out = ShortArray((2.4 * sampleRate).toInt())
        note(out, 0.0, 1.1, 880.0, 0.55, sampleRate, partials = 3)
        out
    }

    Settings.SOUND_LOW_PULSE -> {
        val out = ShortArray((2.0 * sampleRate).toInt())
        note(out, 0.0, 0.5, 220.0, 0.85, sampleRate, partials = 4)
        note(out, 0.55, 0.5, 185.0, 0.85, sampleRate, partials = 4) // falls: reads as "wrong"
        out
    }

    Settings.SOUND_URGENT_BEEP -> {
        val out = ShortArray((1.6 * sampleRate).toInt())
        for (k in 0 until 4) note(out, k * 0.18, 0.12, 1000.0, 0.9, sampleRate, partials = 2)
        out
    }

    Settings.SOUND_SIREN -> {
        val out = ShortArray((1.8 * sampleRate).toInt())
        sweep(out, 0.0, 0.6, 600.0, 1300.0, 0.95, sampleRate)
        sweep(out, 0.6, 0.6, 600.0, 1300.0, 0.95, sampleRate)
        out
    }

    else -> { // SOUND_RISING_CHIME (default): three ascending notes — calm, but it climbs at you
        val out = ShortArray((2.2 * sampleRate).toInt())
        note(out, 0.0, 0.45, 660.0, 0.7, sampleRate, partials = 3)
        note(out, 0.30, 0.45, 880.0, 0.7, sampleRate, partials = 3)
        note(out, 0.60, 0.75, 1100.0, 0.75, sampleRate, partials = 3)
        out
    }
}

/**
 * ALRM-14: how loud the alarm should be [elapsedMs] into ringing, as a fraction of the user's
 * chosen volume. It starts gentle and reaches full volume within a few seconds — enough to wake
 * without a jolt, never so gentle that it fails to wake.
 */
fun alarmRampGain(elapsedMs: Long, rampMs: Long = 5_000): Double {
    if (elapsedMs >= rampMs) return 1.0
    val p = (elapsedMs.coerceAtLeast(0).toDouble() / rampMs)
    return START_GAIN + (1.0 - START_GAIN) * p
}

/** Never start silent: a first cycle nobody hears is a first cycle wasted. */
private const val START_GAIN = 0.35
