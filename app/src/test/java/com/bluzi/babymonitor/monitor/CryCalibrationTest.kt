package com.bluzi.babymonitor.monitor

import com.bluzi.babymonitor.data.Settings
import com.bluzi.babymonitor.dsp.SR
import com.bluzi.babymonitor.dsp.analyzeWindow
import com.bluzi.babymonitor.dsp.babyCry
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

// ALRM-2 (one sensitivity dial) and ALRM-15/16/17 (learning from the parent's answers).
// The mapping and the learning are pure logic; detector integration runs the REAL analysis.

private const val WINDOW = 2048
private const val WINDOW_MS = WINDOW * 1000L / SR

/** Feed [seconds] of a real synthesised cry to a detector and report whether it ever alarmed. */
private fun cryFires(thresholdDb: Double, levelDb: Double, seconds: Double = 4.0): Boolean {
    val detector = BabyNoiseDetector(enabled = true, thresholdDb = thresholdDb)
    var now = 0L
    var fired = false
    val windows = (seconds * SR / WINDOW).toInt()
    val pcm = babyCry(WINDOW * windows)
    for (w in 0 until windows) {
        val chunk = pcm.copyOfRange(w * WINDOW, (w + 1) * WINDOW)
        now += WINDOW_MS
        if (detector.onWindow(levelDb, analyzeWindow(chunk, SR), WINDOW_MS, now)) fired = true
    }
    return fired
}

class CryCalibrationTest {
    // --- ALRM-2: the single sensitivity dial ------------------------------------------------

    @Test
    fun `ALRM-2 sensitivity runs 1 to 10 and defaults to the middle`() {
        assertEquals(1, Settings.SENSITIVITY_MIN)
        assertEquals(10, Settings.SENSITIVITY_MAX)
        assertEquals(5, Settings.SENSITIVITY_DEFAULT)
        assertEquals(Settings.SENSITIVITY_DEFAULT, Settings().alarmSensitivity)
    }

    @Test
    fun `ALRM-2 higher sensitivity means quieter sound can trigger`() {
        for (s in Settings.SENSITIVITY_MIN until Settings.SENSITIVITY_MAX) {
            assertTrue(
                "sensitivity ${s + 1} must demand less loudness than $s",
                sensitivityThresholdDb(s + 1) < sensitivityThresholdDb(s),
            )
        }
    }

    @Test
    fun `ALRM-2 a quiet cry alarms at high sensitivity but not at low`() {
        val quietCryDb = 6.0
        assertTrue(cryFires(sensitivityThresholdDb(Settings.SENSITIVITY_MAX), quietCryDb))
        assertFalse(cryFires(sensitivityThresholdDb(Settings.SENSITIVITY_DEFAULT), quietCryDb))
        assertFalse(cryFires(sensitivityThresholdDb(Settings.SENSITIVITY_MIN), quietCryDb))
    }

    @Test
    fun `ALRM-2 a sensitivity outside the scale is treated as its nearest end`() {
        // Stored settings can be old or hand-edited; the mapping must never explode or go wild.
        assertEquals(sensitivityThresholdDb(Settings.SENSITIVITY_MIN), sensitivityThresholdDb(-3), 1e-9)
        assertEquals(sensitivityThresholdDb(Settings.SENSITIVITY_MAX), sensitivityThresholdDb(99), 1e-9)
    }

    // --- ALRM-15: which alarms ask for an answer ---------------------------------------------

    @Test
    fun `ALRM-15 only the crying alarm asks whether it was a false alarm`() {
        assertTrue(asksForCryFeedback(AlarmKind.BABY_NOISE))
        assertFalse(asksForCryFeedback(AlarmKind.FEED_DOWN))
    }

    @Test
    fun `ALRM-15 the answer goes to the camera that alarmed, once, and only while one is pending`() {
        val applied = mutableListOf<Pair<String, Boolean>>()
        MonitorHub.onCryFeedback = { did, wasCry -> applied += did to wasCry }
        try {
            MonitorHub.submitCryFeedback(true) // nothing pending: a stray tap learns nothing
            assertTrue(applied.isEmpty())

            MonitorHub.pendingCryFeedback.value = "cam-1"
            MonitorHub.pendingCryFeedback.value = "cam-2" // a newer alarm replaces the question
            MonitorHub.submitCryFeedback(false)
            assertEquals(listOf("cam-2" to false), applied)
            assertEquals(null, MonitorHub.pendingCryFeedback.value) // asked once, answered once

            MonitorHub.pendingCryFeedback.value = "cam-1"
            MonitorHub.dismissCryFeedback() // optional: dismissing learns nothing (ALRM-15)
            assertEquals(null, MonitorHub.pendingCryFeedback.value)
            assertEquals(1, applied.size)
        } finally {
            MonitorHub.onCryFeedback = null
            MonitorHub.pendingCryFeedback.value = null
        }
    }

    // --- ALRM-16: bounded learning ------------------------------------------------------------

    @Test
    fun `ALRM-16 a false-alarm answer makes triggering one step harder`() {
        assertEquals(1, afterFalseAlarm(0))
        val before = effectiveThresholdDb(Settings.SENSITIVITY_DEFAULT, 0)
        val after = effectiveThresholdDb(Settings.SENSITIVITY_DEFAULT, afterFalseAlarm(0))
        assertTrue("a false alarm must raise the bar", after > before)

        // Integration: a cry just over the slider's bar fired before the answer, not after.
        val borderline = before + 1.0
        assertTrue(cryFires(before, borderline))
        assertFalse(cryFires(after, borderline))
    }

    @Test
    fun `ALRM-16 a confirmed cry undoes one step`() {
        assertEquals(0, afterRealCry(afterFalseAlarm(0)))
        assertEquals(1, afterRealCry(2))
    }

    @Test
    fun `ALRM-16 learning is bounded and never goes below the slider setting`() {
        // However many false alarms are reported, the steps stop at the cap...
        var steps = 0
        repeat(20) { steps = afterFalseAlarm(steps) }
        assertEquals(CALIBRATION_MAX_STEPS, steps)
        // ...and however many cries are confirmed, tuning never drops below the slider's own bar.
        repeat(20) { steps = afterRealCry(steps) }
        assertEquals(0, steps)
        assertEquals(
            sensitivityThresholdDb(Settings.SENSITIVITY_DEFAULT),
            effectiveThresholdDb(Settings.SENSITIVITY_DEFAULT, 0),
            1e-9,
        )
    }

    @Test
    fun `ALRM-16 corrupt stored steps are treated as their nearest bound`() {
        assertEquals(
            effectiveThresholdDb(Settings.SENSITIVITY_DEFAULT, 0),
            effectiveThresholdDb(Settings.SENSITIVITY_DEFAULT, -7),
            1e-9,
        )
        assertEquals(
            effectiveThresholdDb(Settings.SENSITIVITY_DEFAULT, CALIBRATION_MAX_STEPS),
            effectiveThresholdDb(Settings.SENSITIVITY_DEFAULT, 99),
            1e-9,
        )
    }

    @Test
    fun `ALRM-16 fully tuned-down detection still hears a loud cry — it never learns itself deaf`() {
        val maxTuned = effectiveThresholdDb(Settings.SENSITIVITY_DEFAULT, CALIBRATION_MAX_STEPS)
        assertTrue(cryFires(maxTuned, levelDb = maxTuned + 2.0))
        // And the trigger point stays on the level scale, where it can be seen and tuned.
        for (s in Settings.SENSITIVITY_MIN..Settings.SENSITIVITY_MAX) {
            assertTrue(effectiveThresholdDb(s, CALIBRATION_MAX_STEPS) <= LevelMeter.LEVEL_MAX)
        }
    }
}
