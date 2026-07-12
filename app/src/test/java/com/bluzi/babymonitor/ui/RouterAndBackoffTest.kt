package com.bluzi.babymonitor.ui

import com.bluzi.babymonitor.monitor.RECONNECT_BACKOFF_MS
import com.bluzi.babymonitor.monitor.reconnectDelayMs
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class RouterAndBackoffTest {
    @Test
    fun `APP-1+AUTH-1 no session routes to sign-in before anything else`() {
        assertEquals(Screen.Login, route(hasSession = false, hasDevice = false))
        assertEquals(Screen.Login, route(hasSession = false, hasDevice = true))
    }

    @Test
    fun `APP-1+CAM-1 a session without a camera routes to the picker`() {
        assertEquals(Screen.Devices, route(hasSession = true, hasDevice = false))
    }

    @Test
    fun `APP-1+APP-2+CAM-3 session plus stored camera goes straight to the live feed, asking nothing again`() {
        assertEquals(Screen.Viewer, route(hasSession = true, hasDevice = true))
    }

    @Test
    fun `LIVE-5 reconnect waits start under a second and grow to a capped maximum`() {
        assertTrue(reconnectDelayMs(0) < 1000)
        for (i in 1 until RECONNECT_BACKOFF_MS.size) {
            assertTrue(reconnectDelayMs(i) > reconnectDelayMs(i - 1))
        }
        val cap = RECONNECT_BACKOFF_MS.last()
        assertEquals(cap, reconnectDelayMs(100)) // capped at tens of seconds
        assertTrue(cap in 10_000..60_000)
        // A successful connection resets the schedule to the first wait.
        assertEquals(RECONNECT_BACKOFF_MS.first(), reconnectDelayMs(0))
    }
}
