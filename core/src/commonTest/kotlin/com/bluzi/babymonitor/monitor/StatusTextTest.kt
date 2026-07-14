package com.bluzi.babymonitor.monitor

import kotlin.test.assertEquals
import kotlin.test.Test

class StatusTextTest {
    @Test
    fun `LIVE-4 reconnect status shows whole seconds and counts down`() {
        assertEquals("reconnecting in 5s", reconnectStatus(5000))
        assertEquals("reconnecting in 5s", reconnectStatus(4001)) // rounds up, never "0s early"
        assertEquals("reconnecting in 1s", reconnectStatus(1000))
        assertEquals("reconnecting in 1s", reconnectStatus(1))
    }

    @Test
    fun `APP-3 raw engine statuses map to readable copy`() {
        assertEquals("Starting…", friendlyStatus("idle"))
        assertEquals("Connecting…", friendlyStatus("connecting"))
        assertEquals("Live", friendlyStatus("live"))
        assertEquals("Stopped", friendlyStatus("stopped"))
        assertEquals("Reconnecting in 3s", friendlyStatus("reconnecting in 3s"))
        assertEquals("Connection lost — retrying", friendlyStatus("error: Connection reset by peer"))
    }

    @Test
    fun `LIVE-2 the status line says muted while muted — the icon is never the only clue`() {
        assertEquals("Nursery — Live", statusLine("Nursery", STATUS_LIVE, muted = false))
        assertEquals("Nursery — Live · muted", statusLine("Nursery", STATUS_LIVE, muted = true))
        // Muted is worth saying whatever the connection is doing.
        assertEquals("Camera — Connecting… · muted", statusLine("", STATUS_CONNECTING, muted = true))
    }

    @Test
    fun `BG-8 an expired session is never dressed up as a retryable connection error`() {
        assertEquals("Session expired — open the app to sign in", friendlyStatus(STATUS_SESSION_EXPIRED))
    }

    @Test
    fun `WATCH-11 a failed monitor says it stopped working — never retrying`() {
        assertEquals("Monitoring stopped working — press Resume to restart", friendlyStatus(STATUS_MONITOR_FAILED))
    }

    @Test
    fun `LIVE-12 an unsupported camera is not dressed up as a connection problem`() {
        assertEquals("This camera model isn't supported", friendlyStatus(STATUS_UNSUPPORTED_CAMERA))
    }
}
