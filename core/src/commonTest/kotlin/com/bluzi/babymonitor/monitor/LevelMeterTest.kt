package com.bluzi.babymonitor.monitor

import com.bluzi.babymonitor.data.Settings
import kotlin.math.PI
import kotlin.math.pow
import kotlin.math.sin
import kotlin.random.Random
import kotlin.test.assertEquals
import kotlin.test.assertTrue
import kotlin.test.Test

// LIVE-6: level is dB above an adaptive ambient baseline — room tone reads quiet,
// louder events read immediately.

class LevelMeterTest {
    private fun feed(meter: LevelMeter, rms: Double, seconds: Double, startMs: Long, hz: Int = 10): Pair<Double, Long> {
        var last = 0.0
        var t = startMs
        val steps = (seconds * hz).toInt()
        repeat(steps) {
            t += (1000 / hz).toLong()
            last = meter.process(rms, rms * 1.5, t)
        }
        return last to t
    }

    /**
     * A steady background the way a real camera delivers one. Measured on a real C100 in a quiet
     * room: per-window rms wanders across ±5 dB (sensor self-noise plus opus coding noise near
     * silence), peaks ride ~10 dB above rms, and slow swells (AC cycling) add a couple more.
     * Perfectly constant noise (the [feed] helper) is far too clean to catch the flutter the
     * meter must flatten.
     */
    private fun flutter(
        meter: LevelMeter,
        baseRms: Double,
        seconds: Double,
        startMs: Long,
        rnd: Random,
        hz: Int = 10,
    ): Pair<List<Double>, Long> {
        val levels = mutableListOf<Double>()
        var t = startMs
        repeat((seconds * hz).toInt()) {
            t += (1000 / hz).toLong()
            val swellDb = 1.5 * sin(2 * PI * t / 8000.0) // AC cycling over ~8 s
            // Sum of two uniforms ≈ the measured window-to-window spread (±5 dB, mostly ±2).
            val jitterDb = (rnd.nextDouble() * 5.0 - 2.5) + (rnd.nextDouble() * 5.0 - 2.5)
            val rms = baseRms * 10.0.pow((swellDb + jitterDb) / 20.0)
            val crest = 2.8 + rnd.nextDouble() * 0.8 // peaks ~10 dB over rms, wandering a little
            levels += meter.process(rms, rms * crest, t)
        }
        return levels to t
    }

    @Test
    fun `LIVE-6 residual flutter displays as a quiet room — real levels display unchanged`() {
        assertEquals(0.0, displayLevelDb(0.0), 1e-9)
        assertEquals(0.0, displayLevelDb(1.9), 1e-9) // ambient remainder is not "activity"
        assertEquals(2.0, displayLevelDb(2.0), 1e-9)
        assertEquals(14.7, displayLevelDb(14.7), 1e-9)
    }

    @Test
    fun `LIVE-6 silence sits at zero`() {
        val meter = LevelMeter()
        val (level, _) = feed(meter, 0.0, seconds = 5.0, startMs = 1_000)
        assertEquals(0.0, level, 0.1)
    }

    @Test
    fun `LIVE-6 a loud onset over a quiet room reads immediately`() {
        val meter = LevelMeter()
        val (_, t1) = feed(meter, 0.001, seconds = 20.0, startMs = 1_000) // quiet room
        val (level, _) = feed(meter, 0.3, seconds = 0.5, startMs = t1) // crying starts
        assertTrue(level > 10.0, "expected a strong level, got $level")
    }

    @Test
    fun `LIVE-6 constant background noise adapts back toward quiet`() {
        val meter = LevelMeter()
        // White-noise machine switches on and stays on.
        val (initial, t1) = feed(meter, 0.05, seconds = 3.0, startMs = 1_000)
        val (settled, _) = feed(meter, 0.05, seconds = 120.0, startMs = t1)
        assertTrue(initial >= 0.0, "initially audible ($initial)")
        assertTrue(settled < 2.0, "should settle near the floor, got $settled")
    }

    @Test
    fun `LIVE-6 an event louder than the settled background still stands out`() {
        val meter = LevelMeter()
        val (_, t1) = feed(meter, 0.02, seconds = 60.0, startMs = 1_000) // fan noise, settled
        val (level, _) = feed(meter, 0.4, seconds = 0.5, startMs = t1) // cry over the fan
        assertTrue(level > 8.0, "cry should stand out over settled fan, got $level")
    }

    @Test
    fun `LIVE-6 a fluttering background reads flat — and never loud enough to alarm`() {
        val meter = LevelMeter()
        val rnd = Random(42)
        val (_, t1) = flutter(meter, 0.05, seconds = 60.0, startMs = 1_000, rnd = rnd) // settle in
        val (levels, _) = flutter(meter, 0.05, seconds = 120.0, startMs = t1, rnd = rnd)
        val sorted = levels.sorted()
        val median = sorted[sorted.size / 2]
        assertTrue(median <= 0.5, "a steady room should read ~0, got median $median")
        // The reliability half: ambient flutter alone must never satisfy the alarm's loudness
        // bar, even at the most sensitive setting — quiet rooms don't take defenses away.
        val bar = sensitivityThresholdDb(Settings.SENSITIVITY_MAX)
        assertTrue(sorted.last() < bar, "flutter reached $bar dB (max was ${sorted.last()})")
    }

    @Test
    fun `LIVE-6 a cry onset over a fluttering background still reads immediately`() {
        val meter = LevelMeter()
        val rnd = Random(7)
        val (_, t1) = flutter(meter, 0.05, seconds = 60.0, startMs = 1_000, rnd = rnd)
        val (level, _) = feed(meter, 0.5, seconds = 0.5, startMs = t1)
        assertTrue(level > 10.0, "a cry over flutter should stand out, got $level")
    }

    @Test
    fun `LIVE-6 ongoing crying with breaths between bursts is never absorbed into the baseline`() {
        // The breaths anchor the floor to the ROOM. If the baseline ever tracked the typical
        // recent level instead of the quiet dips, minutes of crying would fade to "usual" and a
        // still-crying baby could fail to re-alarm after an acknowledgment (ALRM-5).
        val meter = LevelMeter()
        val (_, t0) = feed(meter, 0.02, seconds = 60.0, startMs = 1_000) // fan, settled
        var t = t0
        var lastBurst = 0.0
        repeat(112) { // ~3 minutes of 1 s cry bursts with 0.6 s breaths
            val (burst, t1) = feed(meter, 0.4, seconds = 1.0, startMs = t)
            val (_, t2) = feed(meter, 0.02, seconds = 0.6, startMs = t1)
            lastBurst = burst
            t = t2
        }
        assertTrue(lastBurst > 8.0, "after minutes of crying, bursts must still stand out, got $lastBurst")
    }
}
