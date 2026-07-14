package com.bluzi.babymonitor.monitor

import com.bluzi.babymonitor.data.Settings

// ALRM-7: when the noise alarm is armed. Pure — time injected as minutes-of-day.

data class AlarmSchedule(
    val windowed: Boolean,
    val startMinutes: Int = 0,
    val endMinutes: Int = 0,
) {
    /** Is the alarm armed at [minutesOfDay] (0..1439)? Windows may cross midnight. */
    fun isActive(minutesOfDay: Int): Boolean = when {
        !windowed -> true
        startMinutes == endMinutes -> true // degenerate window = always
        startMinutes < endMinutes -> minutesOfDay in startMinutes until endMinutes
        else -> minutesOfDay >= startMinutes || minutesOfDay < endMinutes // crosses midnight
    }

    companion object {
        fun from(s: Settings): AlarmSchedule = AlarmSchedule(
            windowed = s.alarmScheduleMode == Settings.SCHEDULE_WINDOW,
            startMinutes = s.alarmWindowStartMinutes,
            endMinutes = s.alarmWindowEndMinutes,
        )
    }
}
