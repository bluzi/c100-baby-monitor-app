package com.bluzi.babymonitor.monitor

// WATCH-1/2/3/9: alarm when the feed has been dead longer than the grace period, once per outage.
// Pure — fed by a periodic tick with an injected clock.

class StreamWatchdog(
    @Volatile var enabled: Boolean = false,
    @Volatile var graceMs: Long = 30_000,
) {
    companion object {
        /**
         * WATCH-9: the watchdog guards the crying alarm, so it is armed only while the crying
         * alarm could itself ring — its own toggle, the crying-alarm toggle, and the crying
         * alarm's active hours all agree. The engine feeds this into [enabled] every tick;
         * an outage outliving an unarmed stretch then fires as soon as arming returns.
         */
        fun armed(
            watchdogEnabled: Boolean,
            alarmEnabled: Boolean,
            schedule: AlarmSchedule,
            minutesOfDay: Int,
        ): Boolean = watchdogEnabled && alarmEnabled && schedule.isActive(minutesOfDay)
    }

    private var downSinceMs: Long? = null
    private var firedThisOutage = false
    private var wasArmed = false

    /**
     * Report the feed state. Returns true exactly when the alarm should start:
     * the feed has been continuously dead for the grace period and this outage
     * hasn't fired yet. Recovery re-arms (WATCH-3).
     */
    fun onTick(feedAlive: Boolean, nowMs: Long): Boolean {
        // WATCH-9 wins over WATCH-3's one-alarm-per-outage: a new armed window (the crying
        // alarm turned on, or its hours beginning) is a new obligation, even for an outage
        // already alarmed and acknowledged in an earlier window.
        if (enabled && !wasArmed) firedThisOutage = false
        wasArmed = enabled
        if (feedAlive) {
            downSinceMs = null
            firedThisOutage = false
            return false
        }
        val since = downSinceMs ?: nowMs.also { downSinceMs = it }
        if (!enabled || firedThisOutage) return false
        if (nowMs - since < graceMs) return false
        firedThisOutage = true
        return true
    }

    /**
     * WATCH-6: the alarm we asked for never sounded (another alarm was already ringing). Take the
     * fire back so the next tick retries — an unheard alarm must not count as an alarm.
     */
    fun unfire() {
        firedThisOutage = false
    }

    /** Monitoring stopped by the user — a dead feed is expected; forget the outage. */
    fun reset() {
        downSinceMs = null
        firedThisOutage = false
    }
}
