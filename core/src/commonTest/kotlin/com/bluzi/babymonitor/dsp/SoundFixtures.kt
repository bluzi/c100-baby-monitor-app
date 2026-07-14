package com.bluzi.babymonitor.dsp

import kotlin.math.PI
import kotlin.math.exp
import kotlin.math.sin
import kotlin.random.Random

// Synthetic stand-ins for the sounds a nursery actually hears at night (ALRM-3/13). They are not
// recordings, but they carry the physical properties the detector reasons about: an infant's high
// fundamental and bright harmonics; an adult's low fundamental; the high-frequency roll-off that a
// wall or a door imposes; the pitchlessness of fans, rumble and bangs.

const val SR = 48_000

private fun harmonicVoice(
    samples: Int,
    f0: Double,
    harmonics: Int,
    amplitude: Double,
    /** Per-harmonic gain — this is where a wall does its damage. */
    harmonicGain: (index: Int, hz: Double) -> Double,
    seed: Int = 7,
): ShortArray {
    val rnd = Random(seed)
    val out = ShortArray(samples)
    for (i in 0 until samples) {
        val t = i.toDouble() / SR
        var v = 0.0
        for (h in 1..harmonics) {
            val hz = f0 * h
            if (hz >= SR / 2) break
            v += harmonicGain(h, hz) * sin(2 * PI * hz * t)
        }
        v += 0.01 * (rnd.nextDouble() * 2 - 1) // a little breath noise; real sound is never pure
        out[i] = (v * amplitude * 32767).toInt().coerceIn(-32768, 32767).toShort()
    }
    return out
}

/** A baby crying in the room: high fundamental, strong bright harmonics, fully present. */
fun babyCry(samples: Int, f0: Double = 450.0, amplitude: Double = 0.30): ShortArray =
    harmonicVoice(samples, f0, harmonics = 10, amplitude = amplitude, harmonicGain = { h, _ -> 1.0 / h })

/** An adult talking IN the room: same fullness, but an octave-plus lower — not a baby. */
fun adultVoiceInRoom(samples: Int, f0: Double = 120.0, amplitude: Double = 0.30): ShortArray =
    harmonicVoice(samples, f0, harmonics = 30, amplitude = amplitude, harmonicGain = { h, _ -> 1.0 / h })

/**
 * Adults talking in the NEXT ROOM — the false positive that matters. Loud (the user has the
 * threshold at maximum), but a wall has stripped the highs: everything above ~700 Hz is gone.
 */
fun muffledSpeechThroughWall(samples: Int, f0: Double = 130.0, amplitude: Double = 0.45): ShortArray =
    harmonicVoice(samples, f0, harmonics = 30, amplitude = amplitude, harmonicGain = { h, hz ->
        // A wall/door is a low-pass filter: ~12 dB per octave above a few hundred Hz.
        val rolloff = 1.0 / (1.0 + (hz / 500.0) * (hz / 500.0))
        rolloff / h
    })

/**
 * A TV through the wall: like muffled speech, but with a pitch that wanders — still dark, still
 * low. This is the other classic 3am false alarm.
 */
fun mutedTelevisionThroughWall(samples: Int, amplitude: Double = 0.40): ShortArray {
    val rnd = Random(11)
    val out = ShortArray(samples)
    var phase = 0.0
    for (i in 0 until samples) {
        val f0 = 110.0 + 60.0 * sin(2 * PI * 0.7 * i / SR) // speech-like pitch movement
        phase += 2 * PI * f0 / SR
        var v = 0.0
        for (h in 1..20) {
            val hz = f0 * h
            val rolloff = 1.0 / (1.0 + (hz / 450.0) * (hz / 450.0))
            v += (rolloff / h) * sin(phase * h)
        }
        v += 0.05 * (rnd.nextDouble() * 2 - 1)
        out[i] = (v * amplitude * 32767).toInt().coerceIn(-32768, 32767).toShort()
    }
    return out
}

/** Traffic / appliance rumble: loud, but almost all of it below 200 Hz and with no clear pitch. */
fun rumble(samples: Int, amplitude: Double = 0.45): ShortArray {
    val rnd = Random(3)
    val out = ShortArray(samples)
    var lp = 0.0
    for (i in 0 until samples) {
        val white = rnd.nextDouble() * 2 - 1
        lp += 0.02 * (white - lp) // heavy low-pass → rumble
        out[i] = (lp * 6 * amplitude * 32767).toInt().coerceIn(-32768, 32767).toShort()
    }
    return out
}

/** A fan / white-noise machine: broadband, bright even — but utterly pitchless. */
fun whiteNoise(samples: Int, amplitude: Double = 0.35): ShortArray {
    val rnd = Random(5)
    return ShortArray(samples) {
        ((rnd.nextDouble() * 2 - 1) * amplitude * 32767).toInt().coerceIn(-32768, 32767).toShort()
    }
}

/** A door slam: very loud, very bright, very short — and gone. */
fun doorSlam(samples: Int, amplitude: Double = 0.9): ShortArray {
    val rnd = Random(9)
    return ShortArray(samples) { i ->
        val env = exp(-i / (SR * 0.05)) // ~50 ms decay
        ((rnd.nextDouble() * 2 - 1) * env * amplitude * 32767).toInt().coerceIn(-32768, 32767).toShort()
    }
}

fun silence(samples: Int): ShortArray = ShortArray(samples)
