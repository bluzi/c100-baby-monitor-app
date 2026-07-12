package com.bluzi.babymonitor.monitor

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class StreamWatchdogTest {
    /** Tick the dog once per second over [seconds]; returns whether it fired at any tick. */
    private fun tick(dog: StreamWatchdog, alive: Boolean, seconds: Int, startMs: Long): Pair<Boolean, Long> {
        var fired = false
        var t = startMs
        repeat(seconds) {
            t += 1000
            if (dog.onTick(alive, t)) fired = true
        }
        return fired to t
    }

    @Test
    fun `WATCH-1 disabled watchdog never fires`() {
        val dog = StreamWatchdog(enabled = false, graceMs = 5_000)
        val (fired, _) = tick(dog, alive = false, seconds = 120, startMs = 0)
        assertFalse(fired)
    }

    @Test
    fun `WATCH-2 fires once after the grace period, whatever the cause`() {
        val dog = StreamWatchdog(enabled = true, graceMs = 30_000)
        val (fired, t) = tick(dog, alive = true, seconds = 10, startMs = 0)
        assertFalse(fired)

        // Feed dies (first observed at t+1000). Quiet through the grace period…
        val early = tick(dog, alive = false, seconds = 30, startMs = t)
        assertFalse(early.first)
        // …fires once the full grace has elapsed since the outage was first seen…
        assertTrue(dog.onTick(false, early.second + 1000))
        // …and not again for the same outage (WATCH-3).
        val later = tick(dog, alive = false, seconds = 300, startMs = early.second + 1000)
        assertFalse(later.first)
    }

    @Test
    fun `WATCH-2 the grace period is configurable`() {
        val dog = StreamWatchdog(enabled = true, graceMs = 5_000)
        // Outage first observed at t=1000; fires when 5 s have elapsed since (t=6000).
        val (fired, t) = tick(dog, alive = false, seconds = 5, startMs = 0)
        assertFalse(fired)
        assertTrue(dog.onTick(false, t + 1000))
    }

    @Test
    fun `WATCH-3 recovery re-arms the watchdog for the next outage`() {
        val dog = StreamWatchdog(enabled = true, graceMs = 5_000)
        var (fired, t) = tick(dog, alive = false, seconds = 6, startMs = 0)
        assertTrue(fired)

        // Feed comes back, then dies again → a fresh alarm after a fresh grace period.
        t = tick(dog, alive = true, seconds = 3, startMs = t).second
        val again = tick(dog, alive = false, seconds = 6, startMs = t)
        assertTrue(again.first)
    }

    @Test
    fun `WATCH-2 user-stopped monitoring is not an outage`() {
        val dog = StreamWatchdog(enabled = true, graceMs = 5_000)
        tick(dog, alive = false, seconds = 3, startMs = 0)
        dog.reset() // user pressed Stop mid-outage
        val (fired, _) = tick(dog, alive = false, seconds = 4, startMs = 3_000)
        assertFalse(fired) // countdown restarted from the reset
    }

    @Test
    fun `WATCH-6 an alarm that could not sound is retried, not lost`() {
        val dog = StreamWatchdog(enabled = true, graceMs = 5_000)
        val (fired, t) = tick(dog, alive = false, seconds = 6, startMs = 0)
        assertTrue(fired)

        // The ringer was busy with the noise alarm, so nothing actually sounded.
        dog.unfire()
        // The outage still holds, so the very next tick fires again (no fresh grace period).
        assertTrue(dog.onTick(false, t + 1000))
        // And it stays a single alarm from there on (WATCH-3).
        val later = tick(dog, alive = false, seconds = 60, startMs = t + 1000)
        assertFalse(later.first)
    }

    @Test
    fun `WATCH-9 armed only while the crying alarm could itself ring`() {
        val always = AlarmSchedule(windowed = false)
        val night = AlarmSchedule(windowed = true, startMinutes = 19 * 60, endMinutes = 7 * 60)

        assertTrue(StreamWatchdog.armed(watchdogEnabled = true, alarmEnabled = true, schedule = always, minutesOfDay = 12 * 60))
        // The crying-alarm toggle off disables the watchdog outright.
        assertFalse(StreamWatchdog.armed(watchdogEnabled = true, alarmEnabled = false, schedule = always, minutesOfDay = 12 * 60))
        // Outside the crying alarm's active hours the watchdog is unarmed…
        assertFalse(StreamWatchdog.armed(watchdogEnabled = true, alarmEnabled = true, schedule = night, minutesOfDay = 12 * 60))
        // …inside them it is armed (including across midnight).
        assertTrue(StreamWatchdog.armed(watchdogEnabled = true, alarmEnabled = true, schedule = night, minutesOfDay = 23 * 60))
        assertTrue(StreamWatchdog.armed(watchdogEnabled = true, alarmEnabled = true, schedule = night, minutesOfDay = 3 * 60))
        // The watchdog's own toggle is still required (WATCH-1).
        assertFalse(StreamWatchdog.armed(watchdogEnabled = false, alarmEnabled = true, schedule = night, minutesOfDay = 23 * 60))
    }

    @Test
    fun `WATCH-9 a feed still dead when the watchdog arms alarms then`() {
        // Unarmed (crying alarm off / out of hours) while the feed dies: quiet, however long.
        val dog = StreamWatchdog(enabled = false, graceMs = 5_000)
        val (fired, t) = tick(dog, alive = false, seconds = 120, startMs = 0)
        assertFalse(fired)

        // The crying alarm turns on / its window opens, feed still dead past grace: alarm now.
        dog.enabled = true
        assertTrue(dog.onTick(false, t + 1000))
        // Still one alarm per outage from here (WATCH-3).
        assertFalse(tick(dog, alive = false, seconds = 60, startMs = t + 1000).first)
    }

    @Test
    fun `WATCH-9 a new armed window re-alarms an already-acknowledged outage`() {
        // Feed dies in the evening window; the alarm fires and is acknowledged.
        val dog = StreamWatchdog(enabled = true, graceMs = 5_000)
        val (fired, t) = tick(dog, alive = false, seconds = 6, startMs = 0)
        assertTrue(fired)

        // The window closes overnight-into-day (unarmed), the feed still dead the whole time…
        dog.enabled = false
        val quiet = tick(dog, alive = false, seconds = 600, startMs = t)
        assertFalse(quiet.first)

        // …and when the next window opens over the still-dead feed, it alarms again:
        // armed hours must never begin with a dead feed and silence.
        dog.enabled = true
        assertTrue(dog.onTick(false, quiet.second + 1000))
    }

    @Test
    fun `WATCH-3 within one armed stretch an acknowledged outage stays quiet`() {
        val dog = StreamWatchdog(enabled = true, graceMs = 5_000)
        val (fired, t) = tick(dog, alive = false, seconds = 6, startMs = 0)
        assertTrue(fired)
        // Continuously armed: the same outage never fires twice (no arming transition).
        val later = tick(dog, alive = false, seconds = 600, startMs = t)
        assertFalse(later.first)
    }

    @Test
    fun `WATCH-6 a retried alarm still re-arms normally after recovery`() {
        val dog = StreamWatchdog(enabled = true, graceMs = 5_000)
        var (fired, t) = tick(dog, alive = false, seconds = 6, startMs = 0)
        assertTrue(fired)
        dog.unfire()

        // The feed recovers before the retry lands — nothing to alarm about any more.
        t = tick(dog, alive = true, seconds = 2, startMs = t).second
        // A brand-new outage still needs a full grace period before it fires.
        val next = tick(dog, alive = false, seconds = 4, startMs = t)
        assertFalse(next.first)
        assertTrue(dog.onTick(false, next.second + 2000))
    }
}
