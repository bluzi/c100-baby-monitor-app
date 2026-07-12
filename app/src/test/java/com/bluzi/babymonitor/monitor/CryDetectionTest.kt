package com.bluzi.babymonitor.monitor

import com.bluzi.babymonitor.dsp.SR
import com.bluzi.babymonitor.dsp.adultVoiceInRoom
import com.bluzi.babymonitor.dsp.analyzeWindow
import com.bluzi.babymonitor.dsp.babyCry
import com.bluzi.babymonitor.dsp.doorSlam
import com.bluzi.babymonitor.dsp.mutedTelevisionThroughWall
import com.bluzi.babymonitor.dsp.muffledSpeechThroughWall
import com.bluzi.babymonitor.dsp.rumble
import com.bluzi.babymonitor.dsp.silence
import com.bluzi.babymonitor.dsp.whiteNoise
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

// ALRM-3 / ALRM-13. These run the REAL analysis over synthesised versions of what a nursery hears
// at night, at the MOST SENSITIVE setting — where false alarms actually happen.

private const val WINDOW = 2048
private const val WINDOW_MS = WINDOW * 1000L / SR

/** The most sensitive setting the user can pick — where the old detector cried wolf. */
private val MOST_SENSITIVE_DB = sensitivityThresholdDb(com.bluzi.babymonitor.data.Settings.SENSITIVITY_MAX)

/** Feed [seconds] of one sound to a detector and report whether it ever alarmed. */
private fun runSound(
    detector: BabyNoiseDetector,
    sound: (Int) -> ShortArray,
    seconds: Double,
    levelDb: Double = 20.0,
): Boolean {
    var now = 0L
    var fired = false
    val windows = (seconds * SR / WINDOW).toInt()
    val pcm = sound(WINDOW * windows)
    for (w in 0 until windows) {
        val chunk = pcm.copyOfRange(w * WINDOW, (w + 1) * WINDOW)
        now += WINDOW_MS
        val metrics = analyzeWindow(chunk, SR)
        if (detector.onWindow(levelDb, metrics, WINDOW_MS, now)) fired = true
    }
    return fired
}

private fun detector() = BabyNoiseDetector(
    enabled = true,
    thresholdDb = MOST_SENSITIVE_DB,
)

class CryDetectionTest {
    @Test
    fun `ALRM-3 a baby crying in the room triggers the alarm`() {
        assertTrue(runSound(detector(), ::babyCry, seconds = 4.0))
    }

    @Test
    fun `ALRM-3 crying triggers across the whole range of infant pitches`() {
        for (f0 in listOf(300.0, 400.0, 500.0, 600.0)) {
            val fired = runSound(detector(), { babyCry(it, f0 = f0) }, seconds = 4.0)
            assertTrue("a cry at ${f0}Hz must trigger the alarm", fired)
        }
    }

    // --- ALRM-13: the things that must NEVER wake a parent -----------------------------------

    @Test
    fun `ALRM-13 adults talking in the next room never trigger, even at maximum sensitivity`() {
        // The exact complaint: loud, voice-like, but muffled by a wall — and an octave too low.
        assertFalse(runSound(detector(), ::muffledSpeechThroughWall, seconds = 8.0, levelDb = 24.0))
    }

    @Test
    fun `ALRM-13 a television through the wall never triggers`() {
        assertFalse(runSound(detector(), ::mutedTelevisionThroughWall, seconds = 8.0, levelDb = 24.0))
    }

    @Test
    fun `ALRM-13 an adult talking in the room is not a baby`() {
        assertFalse(runSound(detector(), ::adultVoiceInRoom, seconds = 8.0, levelDb = 24.0))
    }

    @Test
    fun `ALRM-13 traffic and appliance rumble never trigger`() {
        assertFalse(runSound(detector(), ::rumble, seconds = 8.0, levelDb = 24.0))
    }

    @Test
    fun `ALRM-13 a fan or white-noise machine never triggers`() {
        assertFalse(runSound(detector(), ::whiteNoise, seconds = 8.0, levelDb = 24.0))
    }

    @Test
    fun `ALRM-13 a door slam never triggers`() {
        assertFalse(runSound(detector(), ::doorSlam, seconds = 4.0, levelDb = 24.0))
    }

    @Test
    fun `ALRM-3 silence never triggers`() {
        assertFalse(runSound(detector(), ::silence, seconds = 4.0, levelDb = 0.0))
    }

    // --- the toggle and the ring lifecycle ----------------------------------------------------

    @Test
    fun `ALRM-3 with the toggle off nothing triggers, however hard the baby cries`() {
        val off = BabyNoiseDetector(enabled = false, thresholdDb = MOST_SENSITIVE_DB)
        assertFalse(runSound(off, ::babyCry, seconds = 6.0))
    }

    @Test
    fun `ALRM-5 a ringing alarm suppresses new triggers until acknowledged`() {
        val det = detector()
        assertTrue(runSound(det, ::babyCry, seconds = 4.0))
        det.suppressed = true // the engine sets this while the alarm rings
        assertFalse(runSound(det, ::babyCry, seconds = 4.0))
    }
}
