package com.bluzi.babymonitor.monitor

import kotlin.test.Test
import kotlin.test.assertFalse
import kotlin.test.assertTrue

/** WATCH-12: audio can be alive while the picture is a photograph. */
class PictureLivenessTest {
    private fun frame(seed: Int, size: Int = 512) = ByteArray(size) { (it * 31 + seed).toByte() }

    @Test
    fun `WATCH-12 a picture that keeps changing is never called frozen`() {
        val p = PictureLiveness()
        var now = 0L
        // A working feed, well past the freeze window: a new picture every 40ms for a minute.
        repeat(1500) {
            p.onFrame(frame(it), nowMs = now)
            now += 40
            assertFalse(p.frozen(now), "a moving picture was called frozen at ${now}ms")
        }
    }

    @Test
    fun `WATCH-12 a picture that stops arriving is frozen`() {
        val p = PictureLiveness()
        p.onFrame(frame(1), nowMs = 0)

        // The stream goes quiet. Nothing arrives, so nothing changes.
        assertFalse(p.frozen(9_000))
        assertTrue(p.frozen(10_001))
    }

    @Test
    fun `WATCH-12 the same picture arriving over and over is frozen`() {
        // The failure that looks most like health — and the reason the frame's timestamp is not
        // trusted: frames keep coming, the socket is busy, the timeline advances, the feed is honestly
        // live, and every one of them is the same photograph. A stream is only moving if its picture is.
        val p = PictureLiveness()
        val stuck = frame(7)
        var now = 0L
        repeat(300) {
            p.onFrame(stuck, nowMs = now)
            now += 40
        }

        assertTrue(p.frozen(now), "a repeated frame stood for ${now}ms and was not called frozen")
    }

    @Test
    fun `WATCH-12 one changed byte is a moving picture`() {
        // The picture carries a clock, so a working feed cannot repeat a frame — but the difference
        // between one second and the next may be small. Movement must not need a big diff to count.
        val p = PictureLiveness()
        val a = frame(1)
        val b = a.copyOf().also { it[it.size / 2] = (it[it.size / 2] + 1).toByte() }
        var now = 0L
        repeat(300) {
            p.onFrame(if (it % 2 == 0) a else b, nowMs = now)
            now += 40
            assertFalse(p.frozen(now))
        }
    }

    @Test
    fun `WATCH-12 a camera that never sends a picture is not frozen`() {
        // LIVE-7 / DESK-22: no picture is a gap already said out loud, and audio monitoring is what
        // matters. Reconnecting for ever over a picture that was never coming would take the sound
        // down with it — the one thing that must not happen.
        val p = PictureLiveness()

        assertFalse(p.frozen(60_000))
    }

    @Test
    fun `WATCH-12 a new session starts with a clean slate`() {
        val p = PictureLiveness()
        p.onFrame(frame(1), nowMs = 0)
        assertTrue(p.frozen(20_000))

        p.reset()

        assertFalse(p.frozen(20_000), "a reconnected session inherited the last one's frozen picture")
    }

    @Test
    fun `WATCH-12 the picture is given a few seconds before it is called frozen`() {
        // A blip is not a freeze: a feed that skips a beat must not cost a parent their sound.
        val p = PictureLiveness()
        p.onFrame(frame(1), nowMs = 0)

        assertFalse(p.frozen(1_000))
        assertFalse(p.frozen(PictureLiveness.FREEZE_MS_DEFAULT))
        assertTrue(p.frozen(PictureLiveness.FREEZE_MS_DEFAULT + 1))
    }
}
