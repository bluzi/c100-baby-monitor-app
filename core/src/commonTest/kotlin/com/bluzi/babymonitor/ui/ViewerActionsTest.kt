package com.bluzi.babymonitor.ui

import com.bluzi.babymonitor.monitor.STATUS_LIVE
import com.bluzi.babymonitor.monitor.STATUS_MONITOR_FAILED
import com.bluzi.babymonitor.monitor.STATUS_STOPPED
import kotlin.test.Test
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class ViewerActionsTest {
    @Test
    fun `APP-3+WATCH-11 a failed monitor offers Resume right on the live feed — never a dead end`() {
        // A failed monitor keeps running=true (the watchdog still guards), so Resume must key
        // off the status too — "reopen the app" is not a recovery a half-asleep parent should need.
        assertTrue(ViewerActionKind.Resume in viewerActionKinds(running = true, status = STATUS_MONITOR_FAILED))
        assertTrue(ViewerActionKind.Resume in viewerActionKinds(running = false, status = STATUS_STOPPED))
        assertFalse(ViewerActionKind.Resume in viewerActionKinds(running = true, status = STATUS_LIVE))
    }

    @Test
    fun `BG-11 the live feed offers Stop while monitoring runs — and Resume instead once stopped`() {
        assertTrue(ViewerActionKind.Stop in viewerActionKinds(running = true, status = STATUS_LIVE))
        assertFalse(ViewerActionKind.Stop in viewerActionKinds(running = false, status = STATUS_STOPPED))
        // A failed monitor is still running (the watchdog guards), so it can be resumed OR
        // stopped outright — both controls show.
        assertTrue(ViewerActionKind.Stop in viewerActionKinds(running = true, status = STATUS_MONITOR_FAILED))
    }

    @Test
    fun `LIVE-2+LIVE-9 mute and night vision and alerts stay reachable whatever the monitor is doing`() {
        for (running in listOf(true, false)) {
            val kinds = viewerActionKinds(running, if (running) STATUS_LIVE else STATUS_STOPPED)
            assertTrue(ViewerActionKind.Mute in kinds)
            assertTrue(ViewerActionKind.NightVision in kinds)
            assertTrue(ViewerActionKind.Alerts in kinds)
        }
    }
}
