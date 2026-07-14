package com.bluzi.babymonitor.apple

import com.bluzi.babymonitor.monitor.STATUS_CONNECTING
import com.bluzi.babymonitor.monitor.STATUS_LIVE
import com.bluzi.babymonitor.monitor.STATUS_MONITOR_FAILED
import com.bluzi.babymonitor.monitor.STATUS_SESSION_EXPIRED
import com.bluzi.babymonitor.monitor.STATUS_STOPPED
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

/**
 * The Mac shell's decisions (MACOS-14/16, LIVE-11m). They live here, in the shared core's Apple
 * source set, for the same reason every other decision does: a rule that is only written in a view
 * is a rule nobody can test, and this one guards the app's first promise — that a warning is never
 * hidden, and silence is never mistaken for a calm baby.
 */
class MacShellTest {
    private val healthy = MonitorHealth(
        running = true,
        status = STATUS_LIVE,
        activeAlarm = null,
        sessionExpired = false,
        sleepOutage = null,
    )

    // --- MACOS-16: what "needs attention" means ------------------------------

    @Test
    fun `MACOS-16 a live healthy monitor needs no attention`() {
        assertFalse(MacShell.needsAttention(healthy))
    }

    @Test
    fun `MACOS-16 a ringing alarm needs attention`() {
        assertTrue(MacShell.needsAttention(healthy.copy(activeAlarm = "BABY_NOISE")))
        assertTrue(MacShell.needsAttention(healthy.copy(activeAlarm = "FEED_DOWN")))
    }

    @Test
    fun `MACOS-16 a feed that is not live while monitoring needs attention`() {
        assertTrue(MacShell.needsAttention(healthy.copy(status = STATUS_CONNECTING)))
        assertTrue(MacShell.needsAttention(healthy.copy(status = "reconnecting in 4s")))
        assertTrue(MacShell.needsAttention(healthy.copy(status = "error: connection reset")))
        assertTrue(MacShell.needsAttention(healthy.copy(status = STATUS_MONITOR_FAILED)))
    }

    @Test
    fun `MACOS-16 a stopped monitor needs attention`() {
        // The most dangerous quiet state of all: a picture still on screen and nothing watching it.
        assertTrue(MacShell.needsAttention(healthy.copy(running = false, status = STATUS_STOPPED)))
    }

    @Test
    fun `MACOS-16 an expired session needs attention`() {
        assertTrue(MacShell.needsAttention(healthy.copy(sessionExpired = true)))
        assertTrue(MacShell.needsAttention(healthy.copy(status = STATUS_SESSION_EXPIRED)))
    }

    @Test
    fun `MACOS-16 a sleep outage still unread needs attention`() {
        assertTrue(MacShell.needsAttention(healthy.copy(sleepOutage = "The Mac slept for 8 minutes.")))
    }

    // --- MACOS-16: the fade itself -------------------------------------------

    @Test
    fun `MACOS-16 the mini fades only when nothing is wrong and the pointer is away`() {
        assertEquals(
            0.4,
            MacShell.miniOpacity(healthy, hovering = false, fadeEnabled = true, reduceTransparency = false, idleOpacity = 0.4),
        )
    }

    @Test
    fun `MACOS-16 the pointer over the mini makes it solid`() {
        assertEquals(
            1.0,
            MacShell.miniOpacity(healthy, hovering = true, fadeEnabled = true, reduceTransparency = false, idleOpacity = 0.4),
        )
    }

    @Test
    fun `MACOS-16 a mini that needs attention never fades however faint the setting`() {
        val alarming = healthy.copy(activeAlarm = "BABY_NOISE")
        assertEquals(
            1.0,
            MacShell.miniOpacity(alarming, hovering = false, fadeEnabled = true, reduceTransparency = false, idleOpacity = 0.25),
        )
        val dead = healthy.copy(status = "error: connection reset")
        assertEquals(
            1.0,
            MacShell.miniOpacity(dead, hovering = false, fadeEnabled = true, reduceTransparency = false, idleOpacity = 0.25),
        )
    }

    @Test
    fun `MACOS-16 fading can be turned off`() {
        assertEquals(
            1.0,
            MacShell.miniOpacity(healthy, hovering = false, fadeEnabled = false, reduceTransparency = false, idleOpacity = 0.3),
        )
    }

    @Test
    fun `MACOS-18 Reduce Transparency turns the fade off`() {
        assertEquals(
            1.0,
            MacShell.miniOpacity(healthy, hovering = false, fadeEnabled = true, reduceTransparency = true, idleOpacity = 0.3),
        )
    }

    @Test
    fun `MACOS-16 the mini can never be set so faint that it cannot be seen`() {
        // A stored 0 — an old build a hand-edited plist a slider dragged to the floor — must not
        // produce an invisible monitor.
        assertEquals(MacShell.MINI_OPACITY_MIN, MacShell.clampMiniOpacity(0.0))
        assertEquals(MacShell.MINI_OPACITY_MIN, MacShell.clampMiniOpacity(-3.0))
        assertEquals(MacShell.MINI_OPACITY_MAX, MacShell.clampMiniOpacity(2.0))
        assertEquals(0.5, MacShell.clampMiniOpacity(0.5))
        assertEquals(MacShell.MINI_OPACITY_DEFAULT, MacShell.clampMiniOpacity(Double.NaN))

        // And the clamp is not something the caller can forget: the opacity is clamped on the way out.
        assertEquals(
            MacShell.MINI_OPACITY_MIN,
            MacShell.miniOpacity(healthy, hovering = false, fadeEnabled = true, reduceTransparency = false, idleOpacity = 0.0),
        )
    }

    // --- MACOS-14: which shape the one window is in --------------------------

    @Test
    fun `MACOS-14 the viewer keeps whichever shape the user chose`() {
        assertEquals(MacShell.SHAPE_MINI, MacShell.windowShape(screen = "viewer", preferred = MacShell.SHAPE_MINI))
        assertEquals(MacShell.SHAPE_FULL, MacShell.windowShape(screen = "viewer", preferred = MacShell.SHAPE_FULL))
    }

    @Test
    fun `MACOS-14 signing in or picking a camera is never done in a tile`() {
        // There is no video to float and there are fields to type into: the window goes full.
        assertEquals(MacShell.SHAPE_FULL, MacShell.windowShape(screen = "login", preferred = MacShell.SHAPE_MINI))
        assertEquals(MacShell.SHAPE_FULL, MacShell.windowShape(screen = "devices", preferred = MacShell.SHAPE_MINI))
    }

    @Test
    fun `MACOS-14 an unknown stored shape is full rather than nothing`() {
        assertEquals(MacShell.SHAPE_FULL, MacShell.windowShape(screen = "viewer", preferred = "gibberish"))
    }
}
