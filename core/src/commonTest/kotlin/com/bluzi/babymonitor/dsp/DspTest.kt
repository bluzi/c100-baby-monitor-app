package com.bluzi.babymonitor.dsp

import kotlin.math.PI
import kotlin.math.sin
import kotlin.test.assertEquals
import kotlin.test.assertTrue
import kotlin.test.Test

// The measurements the crying alarm reasons about (ALRM-3): loudness, pitch, tonality, brightness.

class DspTest {
    private fun sine(freqHz: Double, sampleRate: Int, n: Int, amplitude: Double = 0.5): ShortArray =
        ShortArray(n) { i -> (amplitude * 32767 * sin(2 * PI * freqHz * i / sampleRate)).toInt().toShort() }

    @Test
    fun `analyzeWindow reports loudness`() {
        val metrics = analyzeWindow(sine(1000.0, 48000, 2048), 48000)
        assertEquals(0.5 / kotlin.math.sqrt(2.0), metrics.rms, 0.02)
        assertEquals(0.5, metrics.peak, 0.02)
    }

    @Test
    fun `ALRM-3 pitch is measured — and a baby's pitch is told apart from an adult's`() {
        val baby = analyzeWindow(sine(450.0, 48000, 4096), 48000)
        assertEquals(450.0, baby.pitchHz, 25.0)
        assertTrue(baby.pitchHz in CRY_PITCH_MIN_HZ..CRY_PITCH_MAX_HZ, "a baby's pitch is inside the cry range")

        val adult = analyzeWindow(sine(120.0, 48000, 4096), 48000)
        assertEquals(120.0, adult.pitchHz, 12.0)
        assertTrue(adult.pitchHz < CRY_PITCH_MIN_HZ, "an adult's pitch is below the cry range")
    }

    @Test
    fun `ALRM-3 pitch does not drop an octave and mistake a baby for an adult`() {
        // A periodic signal also correlates at multiples of its period. Picking the wrong one would
        // report a crying baby at half her pitch — squarely in the adult range — and drop the cry.
        val cry = analyzeWindow(babyCry(4096, f0 = 500.0), 48000)
        assertTrue(cry.pitchHz in CRY_PITCH_MIN_HZ..CRY_PITCH_MAX_HZ, "reported ${cry.pitchHz}Hz")
    }

    @Test
    fun `ALRM-3 a clear tone is tonal — noise is not`() {
        assertTrue(analyzeWindow(sine(450.0, 48000, 4096), 48000).tonality > 0.8)
        assertTrue(analyzeWindow(whiteNoise(4096), 48000).tonality < 0.3)
    }

    @Test
    fun `ALRM-13 a wall takes the brightness away — that is how muffled speech is spotted`() {
        val inRoom = analyzeWindow(babyCry(4096), 48000)
        val throughWall = analyzeWindow(muffledSpeechThroughWall(4096), 48000)
        assertTrue(inRoom.brightness > 0.12, "in-room cry is bright (${inRoom.brightness})")
        assertTrue(throughWall.brightness < 0.12, "muffled speech is dark (${throughWall.brightness})")
    }

    @Test
    fun `ALRM-13 rumble piles its energy at the bottom — a cry does not`() {
        assertTrue(analyzeWindow(rumble(4096), 48000).lowRatio > 0.55)
        assertTrue(analyzeWindow(babyCry(4096), 48000).lowRatio < 0.55)
    }

    @Test
    fun `silence has no loudness and no pitch`() {
        val metrics = analyzeWindow(ShortArray(2048), 48000)
        assertEquals(0.0, metrics.rms, 1e-9)
        assertEquals(0.0, metrics.pitchHz, 1e-9)
    }
}
