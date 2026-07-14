package com.bluzi.babymonitor.monitor

import com.bluzi.babymonitor.data.Settings
import kotlin.test.assertFalse
import kotlin.test.assertTrue
import kotlin.test.Test

private fun min(h: Int, m: Int = 0) = h * 60 + m

class AlarmScheduleTest {
    @Test
    fun `ALRM-7 always mode is armed at any time of day`() {
        val schedule = AlarmSchedule(windowed = false)
        for (t in intArrayOf(0, min(3), min(12), min(19, 30), min(23, 59))) {
            assertTrue(schedule.isActive(t))
        }
    }

    @Test
    fun `ALRM-7 a same-day window arms only inside it`() {
        val schedule = AlarmSchedule(windowed = true, startMinutes = min(8), endMinutes = min(17))
        assertTrue(schedule.isActive(min(8)))
        assertTrue(schedule.isActive(min(12)))
        assertTrue(schedule.isActive(min(16, 59)))
        assertFalse(schedule.isActive(min(7, 59)))
        assertFalse(schedule.isActive(min(17)))
        assertFalse(schedule.isActive(min(23)))
    }

    @Test
    fun `ALRM-7 a window crossing midnight arms through the night`() {
        val schedule = AlarmSchedule(windowed = true, startMinutes = min(19), endMinutes = min(7))
        assertTrue(schedule.isActive(min(19)))
        assertTrue(schedule.isActive(min(23, 59)))
        assertTrue(schedule.isActive(0))
        assertTrue(schedule.isActive(min(6, 59)))
        assertFalse(schedule.isActive(min(7)))
        assertFalse(schedule.isActive(min(12)))
        assertFalse(schedule.isActive(min(18, 59)))
    }

    @Test
    fun `ALRM-7 start equal to end means always`() {
        val schedule = AlarmSchedule(windowed = true, startMinutes = min(9), endMinutes = min(9))
        assertTrue(schedule.isActive(min(9)))
        assertTrue(schedule.isActive(min(21)))
    }

    @Test
    fun `ALRM-7 schedule derives from settings`() {
        val always = AlarmSchedule.from(Settings(alarmScheduleMode = Settings.SCHEDULE_ALWAYS))
        assertTrue(always.isActive(min(12)))

        val windowed = AlarmSchedule.from(
            Settings(
                alarmScheduleMode = Settings.SCHEDULE_WINDOW,
                alarmWindowStartMinutes = min(19),
                alarmWindowEndMinutes = min(7),
            ),
        )
        assertTrue(windowed.isActive(min(22)))
        assertFalse(windowed.isActive(min(12)))
    }
}
