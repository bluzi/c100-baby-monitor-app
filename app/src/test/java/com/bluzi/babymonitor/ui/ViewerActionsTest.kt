package com.bluzi.babymonitor.ui

import com.bluzi.babymonitor.monitor.STATUS_LIVE
import com.bluzi.babymonitor.monitor.STATUS_MONITOR_FAILED
import com.bluzi.babymonitor.monitor.STATUS_STOPPED
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class ViewerActionsTest {
    private fun actions(running: Boolean, status: String) = viewerActions(
        muted = false,
        running = running,
        status = status,
        nightVision = null,
        onToggleMute = {},
        onResume = {},
        onNightVision = {},
        onSettings = {},
        onStop = {},
    )

    @Test
    fun `APP-3+WATCH-11 a failed monitor offers Resume right on the live feed, never a dead end`() {
        // A failed monitor keeps running=true (the watchdog still guards), so Resume must key
        // off the status too — "reopen the app" is not a recovery a half-asleep parent should need.
        assertTrue(actions(running = true, status = STATUS_MONITOR_FAILED).any { it.label == "Resume" })
        assertTrue(actions(running = false, status = STATUS_STOPPED).any { it.label == "Resume" })
        assertFalse(actions(running = true, status = STATUS_LIVE).any { it.label == "Resume" })
    }

    @Test
    fun `BG-11 the live feed offers Stop while the service runs, and Resume instead once stopped`() {
        assertTrue(actions(running = true, status = STATUS_LIVE).any { it.label == "Stop monitoring" })
        assertFalse(actions(running = false, status = STATUS_STOPPED).any { it.label == "Stop monitoring" })
        // A failed monitor is still a running service (the watchdog guards) — it can be resumed
        // OR stopped outright, so both controls show.
        assertTrue(actions(running = true, status = STATUS_MONITOR_FAILED).any { it.label == "Stop monitoring" })
    }
}
